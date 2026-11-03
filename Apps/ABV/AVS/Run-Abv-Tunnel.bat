@echo off
rem THE DEMO FOR PEOPLE OUTSIDE YOUR NETWORK, through a Cloudflare quick tunnel (tools\cloudflared.exe, no account). The server itself stays CLOSED (this PC only);
rem the tunnel makes a public https address that leads to it, and nobody can join without the MEETING CODE printed here, which you tell your guests.
rem Close both windows (this one and "ABV server") when the meeting is over: the address dies with them.
setlocal
for /f %%i in ('powershell -NoProfile -Command "Get-Random -Minimum 100000 -Maximum 999999"') do set ABV_CODE=%%i
echo.
echo   MEETING CODE: %ABV_CODE%
echo.
start "ABV server" cmd /k "cd /d %~dp0Server && dotnet run --no-build"
timeout /t 12 /nobreak >nul
echo   Look below for the address  https://....trycloudflare.com  and send it to your guests with the code.
"%~dp0tools\cloudflared.exe" tunnel --no-autoupdate --url http://localhost:5180
