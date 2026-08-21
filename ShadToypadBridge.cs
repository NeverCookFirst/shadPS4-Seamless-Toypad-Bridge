// ShadToypadBridge — TCP-мост между приложением LegoToypad (harrysof) и shadPS4.
//
//   LegoToypad.exe ──TCP 127.0.0.1:9191──> [этот мост] ──stdin/IPC──> shadPS4.exe
//
// Протокол приложения (тот же, что у Cemu-форка и нашего RPCS3-слушателя):
//   LOAD   : 5 байт (0x01, pad, index, 0, 0) + 180 байт тега + u16le длина пути + путь UTF-8
//   REMOVE : 5 байт (0x02, pad, index, 0, 0)
//   MOVE   : 5 байт (0x03, destPad, destIndex, srcPad, srcIndex)
//   pad: 1=центр, 2=лево, 3=право; index: 0..6. Одно соединение на сообщение.
//
// IPC shadPS4 (SHADPS4_ENABLE_IPC=true): команды в stdin, ответы/хендшейк в stderr,
// разделитель — '\n' (не CRLF!). После ";#IPC_END" эмулятор ждёт RUN (≤5 сек), затем
// START перед запуском игры.
//
// Собирается штатным csc.exe из .NET Framework (C# 5): см. build.cmd.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class ShadToypadBridge
{
    const int DefaultPort = 9191;
    const int TagSize = 180; // 0x2D блоков * 4 байта

    static Stream emuStdin;                // сырой stdin эмулятора, защищён lock(stdinLock)
    static readonly object stdinLock = new object();
    static Process emu;
    static int tempCounter;                // уникальные имена temp-файлов тегов

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // .NET Framework: обращение к Process.StandardInput создаёт StreamWriter с
        // AutoFlush=true, и тот сразу флашит преамбулу Console.InputEncoding в пайп.
        // Если кодировка консоли UTF-8 (chcp 65001), эмулятор получит BOM перед первой
        // командой ("<BOM>RUN" -> UNKNOWN CMD). Ставим UTF-8 без BOM заранее.
        try { Console.InputEncoding = new UTF8Encoding(false); } catch (Exception) { }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ShadToypadBridge.exe <path-to-shadPS4.exe> <path-to-eboot.bin> [port]");
            return 2;
        }

        string emuPath = args[0];
        string gamePath = args[1];
        int port = DefaultPort;
        if (args.Length >= 3 && !int.TryParse(args[2], out port))
        {
            Console.Error.WriteLine("[bridge] Bad port: " + args[2]);
            return 2;
        }

        if (!File.Exists(emuPath)) { Console.Error.WriteLine("[bridge] shadPS4.exe not found: " + emuPath); return 2; }
        if (!File.Exists(gamePath)) { Console.Error.WriteLine("[bridge] game not found: " + gamePath); return 2; }

        EnsureDimensionsBackend();
        CleanupTempTags();

        // ── запуск shadPS4 с IPC ─────────────────────────────────────────────
        var psi = new ProcessStartInfo();
        psi.FileName = emuPath;
        psi.Arguments = "--game \"" + gamePath + "\"";
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;   // IPC-команды
        psi.RedirectStandardError = true;   // IPC-хендшейк/ответы
        psi.RedirectStandardOutput = false; // лог эмулятора остаётся в нашей консоли
        psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(emuPath));
        psi.EnvironmentVariables["SHADPS4_ENABLE_IPC"] = "true";

        emu = new Process();
        emu.StartInfo = psi;
        if (!emu.Start())
        {
            Console.Error.WriteLine("[bridge] Could not start shadPS4");
            return 1;
        }

        // ВАЖНО: IPC парсит строго по '\n', а StreamWriter при первом Flush записал бы
        // UTF-8 BOM (эмулятор увидит "<BOM>RUN" -> UNKNOWN CMD). Поэтому пишем сырые
        // байты в BaseStream: Encoding.UTF8.GetBytes BOM не добавляет.
        emuStdin = emu.StandardInput.BaseStream;
        // Страховка: если какая-то преамбула всё же утекла в пайп, закрываем её '\n' —
        // пустые строки IPC пропускает молча, а "<BOM>" отдельной строкой безвреден.
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

    // ── stderr эмулятора: хендшейк + пробрасывание строк ────────────────────
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
                    // RUN — продолжить запуск эмулятора, START — запустить игру.
                    // binary_semaphore хранит release, поэтому слать оба сразу безопасно.
                    SendIpc("RUN\nSTART\n");
                    Console.WriteLine("[bridge] IPC handshake done (RUN+START sent)");
                }
            }
        }
        catch (Exception) { /* эмулятор закрылся */ }
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

    // ── TCP-слушатель протокола LegoToypad ──────────────────────────────────
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

                // LoadFigure в shadPS4 читает 180 байт из файла (и держит его открытым
                // для записей игры). Если приложение путь не прислало (встроенная
                // фигурка) или файл негодный — сохраняем тег во временный .bin.
                string figurePath = appPath;
                bool usable = false;
                if (figurePath.Length > 0)
                {
                    try { usable = new FileInfo(figurePath).Length >= TagSize; }
                    catch (Exception) { usable = false; }
                }
                if (!usable)
                {
                    int n = Interlocked.Increment(ref tempCounter);
                    figurePath = Path.Combine(Path.GetTempPath(),
                        string.Format("shadps4-toypad-{0}-{1}.bin", index, n));
                    File.WriteAllBytes(figurePath, tag);
                }

                // Контракт приложения (как у Cemu-форка): LOAD молча перезаписывает
                // занятый слот. REMOVE по пустому слоту в shadPS4 — безопасный no-op.
                SendIpc("USB_REMOVE_FIGURE\n" + pad + "\n" + index + "\n1\n" +
                        "USB_LOAD_FIGURE\n" + figurePath + "\n" + pad + "\n" + index + "\n");
                Console.WriteLine(string.Format("[bridge] LOAD -> pad={0} index={1} ({2})",
                    pad, index, appPath.Length > 0 ? appPath : "embedded tag"));
                break;
            }
            case 0x02: // REMOVE
            {
                SendIpc("USB_REMOVE_FIGURE\n" + pad + "\n" + index + "\n1\n");
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

    // ── config.json: включить эмулированный Toypad (usb_device_backend = 3) ─
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
            foreach (string f in Directory.GetFiles(Path.GetTempPath(), "shadps4-toypad-*.bin"))
                try { File.Delete(f); } catch (Exception) { /* держит прошлый инстанс */ }
        }
        catch (Exception) { }
    }
}
