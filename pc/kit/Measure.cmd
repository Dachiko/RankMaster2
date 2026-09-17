@echo off
REM A-startup-and-shell.md § 6.6: double-click fallback for Measure-Startup.ps1, for an owner whose
REM terminal policy blocks "irm | iex".
powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0Measure-Startup.ps1" %*
pause
