@echo off
REM Wrapper that runs setup-windows.ps1 without requiring users to fight
REM PowerShell's execution policy. Pass-through args.

if "%~1"=="" (
  echo usage: %~n0 ^<host-ip^> [path-to-cuphead-folder]
  echo example: %~n0 192.168.0.4
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-windows.ps1" %*
exit /b %ERRORLEVEL%
