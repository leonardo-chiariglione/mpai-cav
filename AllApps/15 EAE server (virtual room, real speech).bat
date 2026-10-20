@echo off
rem THE ENHANCED AUDIOCONFERENCE SERVER with a virtual room of two real voices: open http://localhost:5190/ as a listener, with headphones. CLOSED: this PC only. Close the window to stop it.
call "%~dp0_settings.bat"
set EAE_DEMO=1
set EAE_SPEECH=D:\AI\Datasets\libri_wav\1272\clip.wav;D:\AI\Datasets\libri_wav\1462\clip.wav
cd /d "%MPAI_ROOT%\Apps\CAE\EAE\Server"
start "" http://localhost:5190/
dotnet run -c Release
pause
