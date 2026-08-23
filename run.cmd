@echo off
rem Start LEGO Dimensions through the bridge (shadPS4 + IPC + Toypad listener on 9191).
rem ShadToypadBridge.exe must sit in the SAME FOLDER as shadPS4.exe (it auto-detects it
rem there). Just edit the eboot.bin path below, then always launch the game with this
rem script instead of the Qt launcher. Afterwards start LegoToypad.exe and open the
rem overlay in-game.
"%~dp0ShadToypadBridge.exe" "C:\path\to\games\CUSA01176\eboot.bin"
pause
