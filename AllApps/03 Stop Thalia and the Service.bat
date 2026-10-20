@echo off
rem CLOSES THE SERVICE, THE DESKTOP CLIENT AND THE BROWSER HOST.
taskkill /IM MasService.exe /F >nul 2>&1
taskkill /IM RcaApp.exe /F >nul 2>&1
taskkill /IM RcaWeb.Host.exe /F >nul 2>&1
echo Done.
pause