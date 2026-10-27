@echo off
rem STARTS THE MPAI-MAS SERVICE if it is not running, and waits until it answers (about a minute: it loads the models).
call "%~dp0_settings.bat"
powershell -NoProfile -Command "if ((Get-NetTCPConnection -LocalPort 5105 -State Listen -ErrorAction SilentlyContinue)) { exit 0 } else { exit 1 }"
if %errorlevel%==0 goto ready
echo Starting the MPAI-MAS Service (it loads the models: about a minute)...
if not exist "%~dp0logs" mkdir "%~dp0logs"
start "MPAI-MAS Service" /min cmd /c "cd /d %MPAI_ROOT% && dotnet run --project Apps\MAS\Service\src\MasService.csproj -- %MPAI_SERVICE_CONFIG% > %~dp0logs\service.log 2>&1"
:wait
powershell -NoProfile -Command "Start-Sleep 3; if ((Get-NetTCPConnection -LocalPort 5105 -State Listen -ErrorAction SilentlyContinue)) { exit 0 } else { exit 1 }"
if not %errorlevel%==0 goto wait
:ready
echo The Service is running at %MPAI_MAS_SERVER%