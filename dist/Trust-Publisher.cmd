@echo off
rem OPTIONAL - only on PCs you own. Makes this PC trust the app's self-signed publisher certificate.
rem Undo with:  Trust-Publisher.cmd remove
if /i "%~1"=="remove" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Trust-Publisher.ps1" -Remove
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Trust-Publisher.ps1"
)
