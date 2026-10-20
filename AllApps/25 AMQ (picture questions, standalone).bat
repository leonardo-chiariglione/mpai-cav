@echo off
rem Runs AMQ (picture questions, standalone) from the source (the first run builds it).
call "%~dp0_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\AMQ\Standalone\App\src\AmqApp.csproj
pause