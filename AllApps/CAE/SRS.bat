@echo off
rem SPEECH RESTORATION SYSTEM: you pick the speech of the speaker and the damaged recording, say where it is damaged and what was said there; it writes the restored speech.
call "%~dp0..\_support\_settings.bat"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\_support\_usc.ps1" -App SRS
if errorlevel 1 pause
