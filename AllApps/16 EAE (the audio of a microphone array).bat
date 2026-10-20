@echo off
rem ENHANCED AUDIOCONFERENCE EXPERIENCE: you pick the capture of a microphone array and its geometry (or make a demonstration room); it finds the speakers and writes where and when each speaks, and the audio of each.
call "%~dp0_settings.bat"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_usc.ps1" -App EAE
if errorlevel 1 pause
