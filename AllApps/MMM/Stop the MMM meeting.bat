@echo off
rem CLOSES AN MMM MEETING: the server, its public address (the guests' links stop working), the window with the links, any leftover Whisper. Ollama and the screen helper stay.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\_support\_stop-mmm.ps1"
echo Done.
pause
