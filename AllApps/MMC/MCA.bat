@echo off
rem Runs MCA (conversation with the avatar, standalone) from the source (the first run builds it).
call "%~dp0..\_support\_settings.bat"
call "%~dp0..\_support\_ollama.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\MCA\Standalone\App\src\McaApp.csproj
pause
