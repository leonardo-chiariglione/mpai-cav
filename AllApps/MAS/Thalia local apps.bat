@echo off
rem THALIA'S LOCAL APPS: lets the seven local icons of Thalia's desktop start the standalone Apps on this PC. Close its window to stop it.
start "Thalia local apps" powershell -NoProfile -NoExit -ExecutionPolicy Bypass -File "%~dp0..\_support\_local-apps.ps1"
