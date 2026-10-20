@echo off
rem THE ENHANCED AUDIOCONFERENCE SERVER with a VIRTUAL ROOM of REAL SPEECH: two recordings of speech (edit the two paths below) are two speakers, one at 60 degrees and one at -90,
rem speaking in turn and together in a noisy room (a simulated six-microphone array). Open http://localhost:5190/ as a listener, with headphones. CLOSED: this PC only. Close the window (or Ctrl+C) to stop it.
set EAE_DEMO=1
set EAE_SPEECH=D:\AI\Datasets\libri_wav\1272\clip.wav;D:\AI\Datasets\libri_wav\1462\clip.wav
cd /d "%~dp0Server"
dotnet run -c Release
