@echo off
rem Build the bridge with the stock .NET Framework compiler (present on any Windows 10/11).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /optimize+ /target:exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /win32icon:"%~dp0app_icon.ico" /out:"%~dp0shadPS4ToypadBridge.exe" "%~dp0shadPS4ToypadBridge.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo OK: %~dp0shadPS4ToypadBridge.exe
