@echo off
rem Start LEGO Dimensions through the bridge (shadPS4 + IPC + Toypad listener on 9191).
rem Edit the two paths below for your setup, then always launch the game with this
rem script instead of the Qt launcher. Afterwards start LegoToypad.exe and open the
rem overlay in-game.
"%~dp0ShadToypadBridge.exe" "C:\path\to\shadPS4.exe" "C:\path\to\games\CUSA01176\eboot.bin"
pause
