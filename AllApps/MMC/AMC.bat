@echo off
rem Runs AMC (affective conversation, standalone) from the source (the first run builds it).
call "%~dp0..\_support\_settings.bat"
call "%~dp0..\_support\_ollama.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\AMC\Standalone\App\src\AmcApp.csproj
pause
