@echo off
rem Build the bridge with the stock .NET Framework compiler (present on any Windows 10/11).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /optimize+ /target:exe /win32icon:"%~dp0app_icon.ico" /out:"%~dp0ShadToypadBridge.exe" "%~dp0ShadToypadBridge.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo OK: %~dp0ShadToypadBridge.exe
