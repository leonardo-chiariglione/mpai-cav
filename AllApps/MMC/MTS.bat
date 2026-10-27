@echo off
rem Runs MTS (translation, standalone) from the source (the first run builds it).
call "%~dp0..\_support\_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\MTS\Standalone\App\src\MtsApp.csproj
pause
