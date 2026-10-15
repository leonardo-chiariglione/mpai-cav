@echo off
rem STOPS EVERYTHING of the demo: the server, its speech recogniser and the tunnel. The tunnel address and the ports close at once.
taskkill /F /IM cloudflared.exe >nul 2>&1
taskkill /F /IM Mpai.Abv.Server.exe >nul 2>&1
taskkill /F /IM whisper-server.exe >nul 2>&1
echo The ABV demo is stopped.
