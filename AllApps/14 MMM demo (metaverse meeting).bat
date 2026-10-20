@echo off
call "%~dp0_settings.bat"
powershell -NoProfile -ExecutionPolicy Bypass -File "%MPAI_ROOT%\MMM\Run-Demo.ps1"
pause