@echo off
rem Builds AirBubble.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem shared\: screen capture and protocol copied from the XREAL Desktop host (Host\src\Capture.cs, Native.cs, Unity ...\Net\Protocol.cs).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo Could not find csc.exe from .NET Framework 4.x & exit /b 1 )
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /unsafe /out:bin\AirBubble.exe ^
  /win32manifest:app.manifest /win32icon:..\Installer\app.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll" ^
  src\*.cs shared\*.cs
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
powershell -NoProfile -ExecutionPolicy Bypass -File ..\Installer\signing\sign.ps1 bin\AirBubble.exe
echo Built bin\AirBubble.exe
