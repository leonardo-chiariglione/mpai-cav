@echo off
rem Runs Store service (repository of AIMs) from the source (the first run builds it).
call "%~dp0_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Store\Service\StoreService.csproj
pause