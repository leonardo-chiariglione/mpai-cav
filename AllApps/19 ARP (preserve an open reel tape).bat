@echo off
rem AUDIO RECORDING PRESERVATION: you pick the digitised audio of a tape (and the film of the playback head, if there is one); it writes the Preservation Master and the Access Copy.
call "%~dp0_settings.bat"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_usc.ps1" -App ARP
if errorlevel 1 pause
