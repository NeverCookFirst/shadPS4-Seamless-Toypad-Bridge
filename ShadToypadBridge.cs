// ShadToypadBridge - TCP bridge between the LegoToypad app (harrysof) and shadPS4.
//
//   LegoToypad.exe --TCP 127.0.0.1:9191--> [this bridge] --stdin/IPC--> shadPS4.exe
//
// The app protocol (the same as the Cemu fork and our RPCS3 listener):
//   LOAD   : 5 bytes (0x01, pad, index, 0, 0) + 180 tag bytes + u16le path length + UTF-8 path
//   REMOVE : 5 bytes (0x02, pad, index, 0, 0)
//   MOVE   : 5 bytes (0x03, destPad, destIndex, srcPad, srcIndex)
//   pad: 1=center, 2=left, 3=right; index: 0..6. One connection per message.
//
// shadPS4 IPC (SHADPS4_ENABLE_IPC=true): commands on stdin, responses/handshake on stderr,
// separator is '\n' (not CRLF!). After ";#IPC_END" the emulator waits for RUN (<=5s), then
// START before the game starts.
//
// Built with the stock csc.exe from .NET Framework (C# 5): see build.cmd.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;

static class ShadToypadBridge
{
    const int DefaultPort = 9191;
    const int TagSize = 180; // 0x2D blocks * 4 bytes

    // Delay (ms) between REMOVE and LOAD when swapping a figure. Needed so the game has time
    // to pick up and process the 0x56 "figure removed" event before the "figure added" event
    // arrives. Without it, two consecutive events for the same slot can desync the game
    // (swap/clear "doesn't register").
    const int RemoveThenLoadDelayMs = 100;
    // A small pause after processing a message so the bridge doesn't pipeline commands back-to-back.
    const int PostMessageSettleMs = 40;

    static Stream emuStdin;                // the emulator's raw stdin, guarded by lock(stdinLock)
    static readonly object stdinLock = new object();
    static Process emu;
    static int tempCounter;                // counter for unique figure file names

    // Folder for per-slot figure files (in %LOCALAPPDATA%\ShadToypadBridge).
    // IMPORTANT: shadPS4 keeps the currently loaded .bin open (ReadWrite), so the bridge NEVER
    // overwrites an existing file - it only creates a new one with a unique name for a different
    // figure, and when reloading the same figure it just reuses the previous path (no write) to
    // preserve game-written data.
    static string slotDir;
    static readonly object slotsLock = new object();

    class SlotState
    {
        public string FilePath;
        public byte[] LastTag;              // the 180 bytes we wrote to FilePath (original, before the game wrote)
        public bool Occupied;
    }
    static readonly SlotState[] slots = new SlotState[7];

    class GameEntry
    {
        public string Title { get; set; }
        public string TitleId { get; set; }
        public string EbootPath { get; set; }
    }

    static string FindActiveEmulator()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            
            // 1. qt_ui.ini
            string iniPath = Path.Combine(appData, "shadPS4QtLauncher", "qt_ui.ini");
            if (File.Exists(iniPath))
            {
                foreach (string line in File.ReadAllLines(iniPath))
                {
                    if (line.StartsWith("versionSelected=", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = line.Substring("versionSelected=".Length).Trim().Replace("\\\\", "\\");
                        if (File.Exists(path)) return path;
                    }
                }
            }

            // 2. versions.json fallback
            string versionsJson = Path.Combine(appData, "shadPS4QtLauncher", "versions.json");
            if (File.Exists(versionsJson))
            {
                string json = File.ReadAllText(versionsJson);
                int pathIdx = json.IndexOf("\"path\"");
                if (pathIdx != -1)
                {
                    int colonIdx = json.IndexOf(":", pathIdx);
                    int startQuote = json.IndexOf("\"", colonIdx);
                    int endQuote = json.IndexOf("\"", startQuote + 1);
                    if (startQuote != -1 && endQuote != -1)
                    {
                        string path = json.Substring(startQuote + 1, endQuote - startQuote - 1).Replace("\\\\", "\\");
                        if (File.Exists(path)) return path;
                    }
                }
            }
        }
        catch { }

        // 3. Fallback to local
        string localEmu = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shadPS4.exe");
        return File.Exists(localEmu) ? localEmu : null;
    }

    static List<GameEntry> DiscoverGames()
    {
        var games = new List<GameEntry>();
        try
        {
            string cfgPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "shadPS4", "config.json");

            if (!File.Exists(cfgPath)) return games;

            string json = File.ReadAllText(cfgPath);
            int generalIdx = json.IndexOf("\"General\"");
            if (generalIdx != -1)
            {
                int installDirsIdx = json.IndexOf("\"install_dirs\"", generalIdx);
                if (installDirsIdx != -1)
                {
                    int startBracket = json.IndexOf("[", installDirsIdx);
                    int endBracket = json.IndexOf("]", installDirsIdx);
                    if (startBracket != -1 && endBracket != -1)
                    {
                        string arrStr = json.Substring(startBracket, endBracket - startBracket);
                        int pathIdx = arrStr.IndexOf("\"path\"");
                        while (pathIdx != -1)
                        {
                            int colonIdx = arrStr.IndexOf(":", pathIdx);
                            int startQuote = arrStr.IndexOf("\"", colonIdx);
                            int endQuote = arrStr.IndexOf("\"", startQuote + 1);
                            if (startQuote != -1 && endQuote != -1)
                            {
                                string dir = arrStr.Substring(startQuote + 1, endQuote - startQuote - 1).Replace("\\\\", "\\");
                                if (Directory.Exists(dir))
                                    ScanDirectoryForGames(dir, games, 0);
                            }
                            pathIdx = arrStr.IndexOf("\"path\"", endQuote);
                        }
                    }
                }
            }
        }
        catch { }
        return games;
    }

    static void ScanDirectoryForGames(string dir, List<GameEntry> games, int depth)
    {
        if (depth > 4) return;
        try
        {
            string eboot = Path.Combine(dir, "eboot.bin");
            if (File.Exists(eboot))
            {
                string dirName = Path.GetFileName(dir);
                string title = dirName;
                string sfoPath = Path.Combine(dir, "sce_sys", "param.sfo");
                if (File.Exists(sfoPath))
                {
                    string parsedTitle = ParseSfoTitle(sfoPath);
                    if (!string.IsNullOrEmpty(parsedTitle)) title = parsedTitle;
                }
                
                games.Add(new GameEntry {
                    Title = title,
                    TitleId = dirName,
                    EbootPath = eboot
                });
            }

            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (!sub.EndsWith("-patch", StringComparison.OrdinalIgnoreCase) &&
                    !sub.EndsWith("-UPDATE", StringComparison.OrdinalIgnoreCase))
                {
                    ScanDirectoryForGames(sub, games, depth + 1);
                }
            }
        }
        catch { }
    }

    static string ParseSfoTitle(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 20 || bytes[0] != 0x00 || bytes[1] != 'P' || bytes[2] != 'S' || bytes[3] != 'F')
                return null;
            
            int keyTableStart = BitConverter.ToInt32(bytes, 8);
            int dataTableStart = BitConverter.ToInt32(bytes, 12);
            int entriesCount = BitConverter.ToInt32(bytes, 16);
            
            for (int i = 0; i < entriesCount; i++)
            {
                int entryOffset = 20 + (i * 16);
                short keyOffset = BitConverter.ToInt16(bytes, entryOffset);
                int dataLen = BitConverter.ToInt32(bytes, entryOffset + 4);
                int dataMaxLen = BitConverter.ToInt32(bytes, entryOffset + 8);
                int dataOffset = BitConverter.ToInt32(bytes, entryOffset + 12);
                
                string key = GetNullTerminatedString(bytes, keyTableStart + keyOffset);
                if (key == "TITLE")
                {
                    return Encoding.UTF8.GetString(bytes, dataTableStart + dataOffset, dataLen).TrimEnd('\0');
                }
            }
        }
        catch { }
        return null;
    }

    static string GetNullTerminatedString(byte[] bytes, int start)
    {
        int end = start;
        while (end < bytes.Length && bytes[end] != 0) end++;
        return Encoding.UTF8.GetString(bytes, start, end - start);
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // .NET Framework: touching Process.StandardInput creates a StreamWriter with
        // AutoFlush=true, which immediately flushes the Console.InputEncoding preamble into the pipe.
        // If the console codepage is UTF-8 (chcp 65001), the emulator receives a BOM before the first
        // command ("<BOM>RUN" -> UNKNOWN CMD). So set UTF-8 without BOM in advance.
        try { Console.InputEncoding = new UTF8Encoding(false); } catch (Exception) { }

        string gamePath = "";
        int port = DefaultPort;
        string emuPath = null;
        
        if (args.Length == 0)
        {
            Console.WriteLine("[bridge] Looking for QtLauncher games & emulator...");
            emuPath = FindActiveEmulator();
            var games = DiscoverGames();
            
            if (games.Count == 0)
            {
                Console.Error.WriteLine("[bridge] No games found in shadPS4 QtLauncher config.");
                Console.Error.WriteLine("[bridge] Usage: ShadToypadBridge.exe <path-to-eboot.bin> [port]");
                return 2;
            }
            else if (games.Count == 1)
            {
                gamePath = games[0].EbootPath;
                Console.WriteLine(string.Format("[bridge] Auto-selected single game: {0} ({1})", games[0].Title, games[0].TitleId));
            }
            else
            {
                Console.WriteLine("\nFound multiple games:");
                for (int i = 0; i < games.Count; i++)
                    Console.WriteLine(string.Format("  [{0}] {1} ({2})", i + 1, games[i].Title, games[i].TitleId));
                    
                Console.Write("\nSelect a game to launch (1-" + games.Count + "): ");
                string choiceStr = Console.ReadLine();
                int choice;
                if (int.TryParse(choiceStr, out choice) && choice >= 1 && choice <= games.Count)
                {
                    gamePath = games[choice - 1].EbootPath;
                }
                else
                {
                    Console.WriteLine("Invalid selection.");
                    return 2;
                }
            }
        }
        else
        {
            gamePath = args[0];
            if (args.Length >= 2 && !int.TryParse(args[1], out port))
            {
                Console.Error.WriteLine("[bridge] Bad port: " + args[1]);
                return 2;
            }
        }
        
        if (emuPath == null) emuPath = FindActiveEmulator();
        
        if (emuPath == null || !File.Exists(emuPath))
        {
            string bridgeDir = AppDomain.CurrentDomain.BaseDirectory;
            Console.Error.WriteLine("[bridge] shadPS4.exe not found via QtLauncher or next to bridge (" + bridgeDir + ")");
            return 2;
        }
        
        if (!File.Exists(gamePath)) { Console.Error.WriteLine("[bridge] game not found: " + gamePath); return 2; }

        EnsureDimensionsBackend();
        InitSlotStorage();
        CleanupTempTags();

        // ── launch shadPS4 with IPC ────────────────────────────────────────
        var psi = new ProcessStartInfo();
        psi.FileName = emuPath;
        psi.Arguments = "--game \"" + gamePath + "\"";
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;   // IPC commands
        psi.RedirectStandardError = true;   // IPC handshake/responses
        psi.RedirectStandardOutput = false; // emulator log stays in our console
        psi.WorkingDirectory = Path.GetDirectoryName(emuPath);
        psi.EnvironmentVariables["SHADPS4_ENABLE_IPC"] = "true";

        emu = new Process();
        emu.StartInfo = psi;
        if (!emu.Start())
        {
            Console.Error.WriteLine("[bridge] Could not start shadPS4");
            return 1;
        }

        // IMPORTANT: the IPC parser splits strictly on '\n', and StreamWriter would write a UTF-8 BOM
        // on the first Flush (the emulator would see "<BOM>RUN" -> UNKNOWN CMD). So we write raw bytes
        // to BaseStream: Encoding.UTF8.GetBytes does not add a BOM.
        emuStdin = emu.StandardInput.BaseStream;
        // Safety: if any preamble still leaked into the pipe, close it with '\n' - the IPC parser
        // silently skips empty lines, and a lone "<BOM>" line by itself is harmless.
        SendIpc("\n");

        var stderrThread = new Thread(StderrPump);
        stderrThread.IsBackground = true;
        stderrThread.Start();

        var listenerThread = new Thread(delegate() { ListenerLoop(port); });
        listenerThread.IsBackground = true;
        listenerThread.Start();

        emu.WaitForExit();
        Console.WriteLine("[bridge] shadPS4 exited with code " + emu.ExitCode + ", closing bridge");
        return emu.ExitCode;
    }

    // ── the emulator's stderr: handshake + passthrough of lines ──────────
    static void StderrPump()
    {
        string line;
        try
        {
            while ((line = emu.StandardError.ReadLine()) != null)
            {
                Console.Error.WriteLine(line);
                if (line.TrimEnd() == ";#IPC_END")
                {
                    // RUN - continue emulator startup, START - start the game.
                    // binary_semaphore holds a release, so sending both at once is safe.
                    SendIpc("RUN\nSTART\n");
                    Console.WriteLine("[bridge] IPC handshake done (RUN+START sent)");
                }
            }
        }
        catch (Exception) { /* emulator closed */ }
    }

    static void SendIpc(string payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        lock (stdinLock)
        {
            emuStdin.Write(bytes, 0, bytes.Length);
            emuStdin.Flush();
        }
    }

    // ── TCP listener for the LegoToypad protocol ─────────────────────────
    static void ListenerLoop(int port)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(4);
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine("[bridge] Cannot listen on 127.0.0.1:" + port + " — " + ex.Message);
            Console.Error.WriteLine("[bridge] Port busy? Change it in LegoToypad.ini and pass as 3rd argument.");
            return;
        }
        Console.WriteLine("[bridge] Toypad listener active on 127.0.0.1:" + port);

        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (Exception) { break; }
            try { HandleClient(client.GetStream()); }
            catch (Exception ex) { Console.Error.WriteLine("[bridge] client error: " + ex.Message); }
            finally { client.Close(); }
        }
    }

    static void HandleClient(NetworkStream ns)
    {
        var header = new byte[5];
        if (!RecvAll(ns, header, 5)) return;

        byte cmd = header[0];
        byte pad = header[1];
        byte index = header[2];

        if (pad < 1 || pad > 3 || index >= 7)
        {
            Console.Error.WriteLine(string.Format("[bridge] Rejected: cmd=0x{0:x2} pad={1} index={2}", cmd, pad, index));
            return;
        }

        switch (cmd)
        {
            case 0x01: // LOAD
            {
                var tag = new byte[TagSize];
                if (!RecvAll(ns, tag, TagSize)) return;

                var lenBuf = new byte[2];
                if (!RecvAll(ns, lenBuf, 2)) return;
                int pathLen = lenBuf[0] | (lenBuf[1] << 8);

                string appPath = "";
                if (pathLen > 0)
                {
                    var pathBuf = new byte[pathLen];
                    if (!RecvAll(ns, pathBuf, pathLen)) return;
                    appPath = Encoding.UTF8.GetString(pathBuf);
                }

                // If the app sent a path to a valid .bin - use it as-is:
                // shadPS4 opens it and writes changes back (persistent, like the GUI).
                bool useAppPath = false;
                if (appPath.Length > 0)
                {
                    try { useAppPath = new FileInfo(appPath).Length >= TagSize; }
                    catch (Exception) { useAppPath = false; }
                }

                // Cemu listener contract: LOAD overwrites an occupied slot. First REMOVE - give the
                // game the "figure removed" event and time to process it, then place the new one
                // (LOAD). Without the pause, two consecutive 0x56 events for the same slot could
                // desync the game (swap/clear doesn't register).
                SendIpc("USB_REMOVE_FIGURE\n" + pad + "\n" + index + "\n1\n");
                if (RemoveThenLoadDelayMs > 0)
                    Thread.Sleep(RemoveThenLoadDelayMs);

                // REMOVE has already closed the previous figure's file, so it's now safe to
                // restage the slot for the new figure (or reuse it - see StageTagToSlot).
                string figurePath = useAppPath ? appPath : StageTagToSlot(index, tag);

                SendIpc("USB_LOAD_FIGURE\n" + figurePath + "\n" + pad + "\n" + index + "\n");

                if (PostMessageSettleMs > 0)
                    Thread.Sleep(PostMessageSettleMs);

                Console.WriteLine(string.Format("[bridge] LOAD -> pad={0} index={1} file={2}",
                    pad, index, useAppPath ? ("path:" + Path.GetFileName(appPath)) : ("slot:" + index)));
                break;
            }
            case 0x02: // REMOVE
            {
                SendIpc("USB_REMOVE_FIGURE\n" + pad + "\n" + index + "\n1\n");
                lock (slotsLock)
                {
                    if (slots[index] != null)
                        slots[index].Occupied = false;
                }
                if (PostMessageSettleMs > 0)
                    Thread.Sleep(PostMessageSettleMs);
                Console.WriteLine(string.Format("[bridge] REMOVE -> pad={0} index={1}", pad, index));
                break;
            }
            case 0x03: // MOVE
            {
                byte srcPad = header[3];
                byte srcIndex = header[4];
                if (srcPad < 1 || srcPad > 3 || srcIndex >= 7)
                {
                    Console.Error.WriteLine(string.Format("[bridge] Rejected MOVE source: pad={0} index={1}", srcPad, srcIndex));
                    return;
                }

                SendIpc("USB_MOVE_FIGURE\n" + pad + "\n" + index + "\n" + srcPad + "\n" + srcIndex + "\n");

                // Move the slot bookkeeping: the destination receives the source figure, the source clears.
                lock (slotsLock)
                {
                    if (index != srcIndex && srcIndex < 7 && index < 7)
                    {
                        slots[index] = slots[srcIndex];
                        slots[srcIndex] = null;
                    }
                }

                if (PostMessageSettleMs > 0)
                    Thread.Sleep(PostMessageSettleMs);
                Console.WriteLine(string.Format("[bridge] MOVE {0}/{1} -> {2}/{3}", srcPad, srcIndex, pad, index));
                break;
            }
            default:
                Console.Error.WriteLine(string.Format("[bridge] Unknown command 0x{0:x2}", cmd));
                break;
        }
    }

    static bool RecvAll(NetworkStream ns, byte[] buf, int len)
    {
        int off = 0;
        while (off < len)
        {
            int got = ns.Read(buf, off, len - off);
            if (got <= 0) return false;
            off += got;
        }
        return true;
    }

    // ── config.json: enable the emulated Toypad (usb_device_backend = 3) ─
    static void EnsureDimensionsBackend()
    {
        string cfg = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "shadPS4", "config.json");
        if (!File.Exists(cfg))
        {
            Console.Error.WriteLine("[bridge] config.json not found (" + cfg + "), set USB backend to Dimensions manually");
            return;
        }
        try
        {
            string text = File.ReadAllText(cfg);
            var re = new Regex("(\"usb_device_backend\"\\s*:\\s*)(\\d+)");
            Match m = re.Match(text);
            if (!m.Success)
            {
                Console.Error.WriteLine("[bridge] usb_device_backend not found in config.json, set it manually (3 = Dimensions)");
                return;
            }
            if (m.Groups[2].Value == "3")
                return; // уже настроено
            File.Copy(cfg, cfg + ".bak-toypad-bridge", true);
            File.WriteAllText(cfg, re.Replace(text, "${1}3", 1));
            Console.WriteLine("[bridge] config.json: usb_device_backend " + m.Groups[2].Value +
                              " -> 3 (Dimensions Toypad), backup: config.json.bak-toypad-bridge");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[bridge] Could not update config.json: " + ex.Message);
        }
    }

    static void CleanupTempTags()
    {
        try
        {
            // Очищаем старые временные файлы прошлых версий (в %TEMP%), текущее хранилище слота — нет.
            foreach (string f in Directory.GetFiles(Path.GetTempPath(), "shadps4-toypad-*.bin"))
                try { File.Delete(f); } catch (Exception) { /* держит прошлый инстанс */ }
        }
        catch (Exception) { }
    }

    // Папка для файлов фигурок по слотам — всегда доступна на запись. При старте
    // очищается от старых файлов прошлых сессий (в том числе от полу-загруженных).
    static void InitSlotStorage()
    {
        try
        {
            slotDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShadToypadBridge");
            Directory.CreateDirectory(slotDir);
            foreach (string f in Directory.GetFiles(slotDir, "*.bin"))
                try { File.Delete(f); } catch (Exception) { /* удерживается */ }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[bridge] Could not create slot storage dir (" + ex.Message +
                                    "), using %TEMP%");
            slotDir = Path.GetTempPath();
        }
    }

    // Готовит файл для shadPS4 и возвращает путь.
    //  - Если это та же фигурка, что уже лежит в слоте, файл НЕ перезаписывается и
    //    просто переиспользуется — так записи игры (сборка/улучшения) переживают
    //    повторную загрузку той же фигурки.
    //  - Для ДРУГОЙ фигурки создаётся НОВЫЙ файл с уникальным именем. Старый файл не
    //    трогаем: shadPS4, пока держит его открытым на запись, блокирует перезапись.
    // Поэтому мы никогда не пишем в файл, который shadPS4 могла оставить открытым, и не
    // ловим "The process cannot access the file ... because it is being used by another process".
    // Вызывается после REMOVE (слот освобождён), но это уже не критично для блокировки.
    static string StageTagToSlot(int index, byte[] tag)
    {
        lock (slotsLock)
        {
            SlotState slot = slots[index];
            if (slot == null)
            {
                slot = new SlotState();
                slots[index] = slot;
            }

            bool sameFigure = slot.LastTag != null && BytesEqual(slot.LastTag, tag);
            if (slot.FilePath == null || !sameFigure || !File.Exists(slot.FilePath))
            {
                int n = Interlocked.Increment(ref tempCounter);
                string path = Path.Combine(slotDir, string.Format("tag-{0}-{1}.bin", index, n));
                try
                {
                    File.WriteAllBytes(path, tag);
                }
                catch (Exception ex)
                {
                    // Слабый fallback: пишем во временный файл, чтобы LOAD всегда имел путь.
                    Console.Error.WriteLine("[bridge] slot file write failed (" + ex.Message +
                                            "), using %TEMP%");
                    path = Path.Combine(Path.GetTempPath(),
                        string.Format("shadps4-toypad-{0}-{1}.bin", index, n));
                    File.WriteAllBytes(path, tag);
                }
                slot.FilePath = path;
                slot.LastTag = tag;
            }

            slot.Occupied = true;
            return slot.FilePath;
        }
    }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }
}
