@echo off
rem STOPS the server and the AIM host of the Module.
taskkill /F /IM EaeServer.exe >nul 2>&1
powershell -NoProfile -Command "Get-CimInstance Win32_Process | ? { $_.CommandLine -match 'AIF.AimHost' } | % { Stop-Process -Id $_.ProcessId -Force }"
echo The Enhanced Audioconference server is stopped.
