@echo off
rem Runs Store app (submit an AIM) from the source (the first run builds it).
call "%~dp0_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Store\App\StoreApp.csproj
pause