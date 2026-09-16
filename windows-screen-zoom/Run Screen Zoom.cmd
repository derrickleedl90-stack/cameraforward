@echo off
setlocal
cd /d "%~dp0"
call build.cmd
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "%~dp0bin\ScreenZoom.exe"
