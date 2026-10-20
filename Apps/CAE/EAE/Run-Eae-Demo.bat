@echo off
rem THE ENHANCED AUDIOCONFERENCE SERVER with a VIRTUAL ROOM (two speakers in a noisy room, in turn and together): open http://localhost:5190/ as a listener, with headphones.
rem CLOSED: it answers on this PC only. Close the window (or Ctrl+C) to stop it.
set EAE_DEMO=1
cd /d "%~dp0Server"
dotnet run -c Release
