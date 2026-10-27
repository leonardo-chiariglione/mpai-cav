@echo off
rem THE ENHANCED AUDIOCONFERENCE SERVER with a virtual room of two real voices: open http://localhost:5190/ as a listener, with headphones. CLOSED: this PC only. Close the window to stop it.
call "%~dp0..\_support\_settings.bat"
set EAE_DEMO=1
set EAE_SPEECH=%MPAI_ROOT%\Test\Data\Speech\clip1272.wav;%MPAI_ROOT%\Test\Data\Speech\clip1462.wav
cd /d "%MPAI_ROOT%\Apps\CAE\EAE\Server"
start "" http://localhost:5190/
dotnet run -c Release
pause
