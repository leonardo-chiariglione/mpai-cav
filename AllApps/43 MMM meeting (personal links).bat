@echo off
call "%~dp0_settings.bat"
rem A meeting in the MMM world: it asks for the chair and the guests, starts the server and a public https address, and prints a personal link for each guest. See MMM\Run-Meeting.ps1.
call "%MPAI_ROOT%\MMM\Run-Meeting.bat" %*
pause
