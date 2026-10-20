@echo off
rem Runs MAT (translation, standalone) from the source (the first run builds it).
call "%~dp0_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\MMC\MAT\Standalone\src\MatApp.csproj
pause