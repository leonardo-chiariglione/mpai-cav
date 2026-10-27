@echo off
rem Runs ACR (access registration, standalone) from the source (the first run builds it).
call "%~dp0..\_support\_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\ACR\Standalone\src\AcrApp.csproj
pause