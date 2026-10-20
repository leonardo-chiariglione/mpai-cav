@echo off
rem Runs MPD (affective dialogue, standalone) from the source (the first run builds it).
call "%~dp0_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\MPD\Standalone\src\MpdApp.csproj
pause