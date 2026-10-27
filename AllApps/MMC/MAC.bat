@echo off
rem Runs MAC (access control, standalone) from the source (the first run builds it).
call "%~dp0..\_support\_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\MAC\Standalone\src\MacApp.csproj
pause