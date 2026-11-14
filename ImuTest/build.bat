@echo off
rem Builds ImuTest.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Run:  ImuTest.exe [seconds] [out.csv]     (set IMU_GUIDED=1 for spoken motion cues)
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /platform:x64 /optimize+ /out:ImuTest.exe /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\System.Speech.dll" ImuTest.cs
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
echo Built ImuTest.exe
