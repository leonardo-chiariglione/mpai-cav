@echo off
rem THE THALIA BROWSER SERVER: the MPAI-MAS Service, then the host of the browser client (its own window, "Thalia browser server"), then the page
rem in your browser. Close the host's window to stop it. What it says is kept in logs\host.log (the Service's in logs\service.log).
rem A server left running from before is closed first, so that what starts is what is on disk now.
call "%~dp0_settings.bat"
echo Closing any Thalia server still running...
taskkill /FI "WINDOWTITLE eq Thalia browser server*" /T /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq MPAI-MAS Service*" /T /F >nul 2>&1
taskkill /IM RcaWeb.Host.exe /F >nul 2>&1
taskkill /IM MasService.exe /F >nul 2>&1
powershell -NoProfile -Command "Start-Sleep 2"
call "%~dp0_service.bat"
start "Thalia browser server" powershell -NoProfile -NoExit -ExecutionPolicy Bypass -File "%~dp0_host.ps1"
powershell -NoProfile -Command "Start-Sleep 15"
start https://localhost:5010/
