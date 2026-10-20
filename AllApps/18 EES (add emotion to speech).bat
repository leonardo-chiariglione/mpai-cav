@echo off
rem EMOTION ENHANCED SPEECH: you pick emotionless speech and an emotion (or a model utterance); it writes the speech with the emotion.
call "%~dp0_settings.bat"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_usc.ps1" -App EES
if errorlevel 1 pause
