@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0Magic5Tray.exe" (
  echo Magic5Tray.exe not found in this folder.
  pause
  exit /b 1
)
start "" "%~dp0Magic5Tray.exe"
