@echo off
cd /d "%~dp0"
if not exist "dist\TH12Coop.exe" (
  echo Run build.ps1 first.
  pause
  exit /b 1
)
start "" "dist\TH12Coop.exe" --offline
