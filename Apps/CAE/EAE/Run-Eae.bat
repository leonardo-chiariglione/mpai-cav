@echo off
rem THE ENHANCED AUDIOCONFERENCE SERVER, CLOSED (this PC only, http://localhost:5190/): the room joins as 'the room' and captures its microphone array (EAE_ARRAY names the array's geometry file), the others as listeners.
cd /d "%~dp0Server"
dotnet run -c Release
