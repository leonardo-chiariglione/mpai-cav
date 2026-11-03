@echo off
rem THE AVATAR VIDEOCONFERENCE SERVER OF THE LIVE DEMO - CLOSED: it answers on this PC only (http://localhost:5180/). Nobody else can reach it.
rem To let others in, use Run-Abv-Open.bat. Close the window (or Ctrl+C) to stop it: the ports close with it.
cd /d "%~dp0Server"
dotnet run -c Release
