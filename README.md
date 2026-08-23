# ShadToypadBridge

A small bridge that connects the controller-driven [**LegoToypad**](https://github.com/harrysof/LegoToypad) companion app to [shadPS4](https://github.com/shadps4-emu/shadPS4)'s built-in emulated LEGO Dimensions Toypad, so figures can be loaded, removed or moved in game straight from a controller.

It is the shadPS4 counterpart of [harrysof/Cemu-2.6-Remote-Toypad-Build](https://github.com/harrysof/Cemu-2.6-Remote-Toypad-Build) and [RPCS3-Seamless-Toypad-Build](https://github.com/NeverCookFirst/RPCS3-Seamless-Toypad-Build) and speaks the same wire protocol, so LegoToypad works with it **unchanged** — the app does not even know which emulator is on the other end of the socket.

Unlike the Cemu and RPCS3 routes, **no emulator fork is needed**. Recent shadPS4 dev builds already ship with both an emulated Dimensions Toypad (VID/PID `0x0E6F:0x0241`) and an IPC interface for controlling it — the bridge simply translates between the app's TCP protocol and shadPS4's IPC commands. Stock emulator, stock app, one small executable in between.

Tested on shadPS4 **v0.18.1 WIP** with LEGO Dimensions **CUSA01176** (app ver 01.24).

## Demo

[![LEGO Dimensions — Seamless Toypad shadPS4 gameplay](https://img.youtube.com/vi/jl-ziDBpHpI/maxresdefault.jpg)](https://www.youtube.com/watch?v=jl-ziDBpHpI)

Swapping figures mid-game straight from the controller — no pausing, no alt-tab, no mouse.

## Why

Loading a figure by hand means alt-tabbing and hunting for a `.bin` file with the mouse every single time. This bridge opens a listener so an external picker app can do it instead, controller in hand, without leaving the game.

## How it works

```
LegoToypad.exe ──TCP 127.0.0.1:9191──> ShadToypadBridge ──stdin/IPC──> shadPS4.exe
   (client)                               (translator)        └─ USB_LOAD/REMOVE/MOVE_FIGURE
```

- The bridge starts shadPS4 with `SHADPS4_ENABLE_IPC=true` and your game's `eboot.bin`, performs the IPC handshake (`RUN` + `START`), and opens a TCP listener bound to `127.0.0.1` only.
- LegoToypad scans your tag library, lets you pick a figure and a Toypad slot with a controller, and sends it over the socket — exactly as it would to the Cemu fork or the RPCS3 build.
- The bridge decodes the message and forwards it to shadPS4 as `USB_LOAD_FIGURE`, `USB_REMOVE_FIGURE` or `USB_MOVE_FIGURE` IPC commands on the emulator's stdin.
- On first run the bridge sets `usb_device_backend` to `3` (Dimensions Toypad) in `%APPDATA%\shadPS4\config.json` automatically — a backup is kept next to it as `config.json.bak-toypad-bridge`.

No authentication, no encryption — it is loopback-only by design.

## Download

Grab the latest zip from [**Releases**](../../releases). Windows x64, needs nothing beyond a working shadPS4 install — the bridge itself runs on the stock .NET Framework present on any Windows 10/11.

## Setup

1. Get a recent [shadPS4](https://github.com/shadps4-emu/shadPS4/releases) dev/pre-release build (the emulated Toypad and the IPC interface are relatively new — v0.18.1 WIP is known good) and make sure LEGO Dimensions runs in it normally first.
2. Extract the zip **directly into your shadPS4 folder**, next to `shadPS4.exe` — the bridge auto-detects the emulator by looking in its own directory, so nothing needs pointing at it manually. Then edit the one remaining path in **`run.cmd`**, to the game's `eboot.bin`.
3. Start `run.cmd`. The game must be launched **through the bridge** (it owns the emulator's stdin/stderr), not through the Qt launcher. Wait for:
   - `[bridge] IPC handshake done (RUN+START sent)`
   - `[bridge] Toypad listener active on 127.0.0.1:9191`
4. Download [LegoToypad](https://github.com/harrysof/LegoToypad) (v1.3, unmodified), put your tag library in a folder named `Lego Dimensions Organized bins` (found automatically by walking up from the executable), and run it. It minimizes to the tray.
5. Open the overlay (default: **Back/Select** on an XInput controller), pick a figure and a pad slot, and it appears in game. The bridge logs every command: `LOAD -> pad=1 index=0 …`.

## Configuration

| Setting | Where | Default |
|---|---|---|
| Listener port | Second command-line argument to `ShadToypadBridge.exe`, after the `eboot.bin` path (see `run.cmd`) | `9191` |

If port 9191 is taken, set the same port in LegoToypad's `LegoToypad.ini` (`[Listener] Port=`) and pass it as the bridge's second argument.

There is no GUI on purpose — the bridge has no state to configure beyond the port.

## Wire protocol

Identical to the Cemu fork and the RPCS3 build. Every message starts with a 5-byte header, one connection per message:

| Offset | Field | Value |
|---|---|---|
| 0 | Command | `0x01` LOAD, `0x02` REMOVE, `0x03` MOVE |
| 1 | Dest pad | 1 = center, 2 = left, 3 = right |
| 2 | Dest slot | 0–6 |
| 3 | Source pad (MOVE only) | 0 for LOAD/REMOVE |
| 4 | Source slot (MOVE only) | 0 for LOAD/REMOVE |

| Command | Payload after header | Forwarded to shadPS4 as |
|---|---|---|
| LOAD `0x01` | 180 raw tag bytes, then a `u16` little-endian path length, then that many UTF-8 path bytes (length may be 0) | `USB_REMOVE_FIGURE` then `USB_LOAD_FIGURE <path> <pad> <slot>` |
| REMOVE `0x02` | none | `USB_REMOVE_FIGURE <pad> <slot> 1` |
| MOVE `0x03` | none | `USB_MOVE_FIGURE <newPad> <newSlot> <oldPad> <oldSlot>` |

If LOAD carries a path, shadPS4 keeps the file open and writes game data back to it — persistent, like a real tag. With no path (an embedded figure) the bridge stages the 180 tag bytes into a `.bin` under `%LOCALAPPDATA%\ShadToypadBridge\`, because shadPS4's `LoadFigure` asserts on reading exactly 180 bytes from a file. Reloading the **same** figure into the same slot reuses that staged file without rewriting it, so game-written data (vehicle builds/upgrades) survives the reload; loading a **different** figure stages a fresh uniquely-named file instead of overwriting, since shadPS4 holds the currently loaded one open with a write lock. Staged files are wiped on the next bridge launch.

LOAD deliberately overwrites an occupied slot (remove first, then load, with a short pause between the two so the game processes the "figure removed" event before the "figure added" one), matching the Cemu listener's contract — `USB_REMOVE_FIGURE` on an empty slot is a safe no-op in shadPS4. Out-of-range pad/slot values and unknown commands are logged and the connection is dropped.

## Notes / caveats

- **Launch through `run.cmd`, not the Qt launcher** — the bridge needs the emulator's stdin/stderr. Normal launcher runs keep working as before, just without figures from the app.
- **XML patches are not auto-loaded in IPC mode** — that is by shadPS4's design (the launcher is expected to push patches itself). For LEGO Dimensions it hardly matters: the community 60 FPS patch (app 01.24) hard-breaks the game anyway — black screen right after the intro cutscene, confirmed by A/B testing.
- **The overlay does not mute gamepad input** — unlike the RPCS3 build, the game still receives button presses while the picker is open. Pause the game first, or bind a keyboard shortcut in LegoToypad's settings.
- `usb_device_backend = 3` also affects normal launches: the game always sees the emulated Toypad instead of a real USB one. If you ever plug in a physical Toypad, set it back to `0`.
- Commands sent before the game starts polling the Toypad (i.e. before the intro finishes) are picked up at the first poll — nothing is lost.
- Error messages inside LegoToypad mention "Cemu" — that is only text, the app is protocol-identical and works fine here.

## The BOM gotcha (for devs integrating with shadPS4 IPC)

.NET Framework writes a UTF-8 BOM into the child's stdin the moment `Process.StandardInput` is first touched (the StreamWriter's `AutoFlush = true` setter flushes the encoding preamble when the console codepage is UTF-8). The emulator then reads `<BOM>RUN`, logs `UNKNOWN CMD` and kills itself after the 5-second RUN-semaphore timeout. The bridge works around it by setting `Console.InputEncoding = new UTF8Encoding(false)` before spawning, writing raw bytes to `BaseStream`, and sending one sacrificial `\n` first (empty lines are silently skipped by the IPC parser). Also note the IPC parser splits strictly on `\n` — LF only, no CRLF.

## Building from source

Nothing to install — the bridge is a single C# 5 file that builds with the stock .NET Framework compiler present on any Windows 10/11:

```
build.cmd
```

(which just runs `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /optimize+` over `ShadToypadBridge.cs`).

## Credits & license

- [shadPS4](https://github.com/shadps4-emu/shadPS4) and its contributors — the emulator, its emulated Toypad and the IPC interface this bridge drives. Nothing in the emulator is modified.
- [harrysof](https://github.com/harrysof) — [LegoToypad](https://github.com/harrysof/LegoToypad) and the [Cemu Remote Toypad build](https://github.com/harrysof/Cemu-2.6-Remote-Toypad-Build) whose protocol this implements.
- The bridge itself is licensed under **MIT** (see [LICENSE](LICENSE)).

Not affiliated with the shadPS4 team, Sony, LEGO or Warner Bros. Interactive. Bring your own game dump and your own tag dumps.
