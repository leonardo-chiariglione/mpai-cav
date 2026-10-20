@echo off
rem SPEECH RESTORATION SYSTEM: you pick the speech of the speaker and the damaged recording, say where it is damaged and what was said there; it writes the restored speech.
call "%~dp0_settings.bat"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_usc.ps1" -App SRS
if errorlevel 1 pause
