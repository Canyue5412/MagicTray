@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0MagicTray.exe" (
  echo MagicTray.exe not found in this folder.
  pause
  exit /b 1
)
start "" "%~dp0MagicTray.exe"
