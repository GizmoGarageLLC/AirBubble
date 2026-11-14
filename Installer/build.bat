@echo off
rem Builds dist\AirBubbleSetup.exe: the viewer, the virtual screens driver and an installer/uninstaller in one file.
rem Uses only the C# compiler that ships with Windows (.NET Framework 4.8).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist app.ico powershell -NoProfile -ExecutionPolicy Bypass -File make_icon.ps1
call ..\Viewer\build.bat || exit /b 1
cd /d "%~dp0"
if not exist ..\dist mkdir ..\dist
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:..\dist\AirBubbleSetup.exe ^
  /win32manifest:setup.manifest /win32icon:app.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /resource:..\Viewer\bin\AirBubble.exe,payload/AirBubble.exe ^
  /resource:..\USER_GUIDE.txt,payload/README.txt ^
  /resource:vdd\MttVDD.dll,payload/vdd/MttVDD.dll ^
  /resource:vdd\mttvdd.cat,payload/vdd/mttvdd.cat ^
  /resource:vdd\MttVDD.inf,payload/vdd/MttVDD.inf ^
  /resource:vdd\vdd_settings.xml,payload/vdd/vdd_settings.xml ^
  /resource:vdd\LICENSE.txt,payload/vdd/LICENSE.txt ^
  Setup.cs VddInstall.cs ..\Viewer\src\UacPrompts.cs
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
powershell -NoProfile -ExecutionPolicy Bypass -File signing\sign.ps1 ..\dist\AirBubbleSetup.exe
echo Built dist\AirBubbleSetup.exe
