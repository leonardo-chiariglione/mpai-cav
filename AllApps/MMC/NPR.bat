@echo off
rem Runs NPR (newspaper reading, standalone) from the source (the first run builds it).
call "%~dp0..\_support\_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\NPR\Standalone\App\src\NprApp.csproj
pause
