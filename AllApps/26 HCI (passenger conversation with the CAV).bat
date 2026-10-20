@echo off
rem Runs HCI (passenger conversation with the CAV) from the source (the first run builds it).
call "%~dp0_settings.bat"
cd /d %MPAI_ROOT%
dotnet run --project Apps\CAV\HCIApps\HciApp\src\HciApp.csproj
pause