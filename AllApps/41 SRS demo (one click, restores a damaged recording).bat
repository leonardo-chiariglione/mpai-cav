@echo off
rem SPEECH RESTORATION SYSTEM, A DEMONSTRATION: a real recording is damaged in two places and restored by the Module with the voice of another speaker; it plays the original, the damaged and the restored one. Needs the voice engine (D:\AI\openvoice) and the LibriSpeech clips (D:\AI\Datasets\libri_wav).
call "%~dp0_settings.bat"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0_srs-demo.ps1"
pause
