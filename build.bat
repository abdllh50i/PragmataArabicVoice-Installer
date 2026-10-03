@echo off
rem Builds PragmataArabicVoice-Installer.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No other tools, packages or internet access are needed.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:PragmataArabicVoice-Installer.exe ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /win32manifest:app.manifest /win32icon:icon.ico src\Installer.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built PragmataArabicVoice-Installer.exe
