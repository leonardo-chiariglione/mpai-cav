@echo off
rem THE MAS APP'S LOCAL APPS: lets the seven local icons of the MAS App's desktop start the standalone Apps on this PC. Close its window to stop it.
start "MAS App local apps" powershell -NoProfile -NoExit -ExecutionPolicy Bypass -File "%~dp0..\_support\_local-apps.ps1"
