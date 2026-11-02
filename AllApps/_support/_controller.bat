@echo off
rem ENSURES THAT A CONTROLLER IS RUNNING (a program of its own) before an App, a User Agent, is started. The Controller starts the Modules and their AIMs; the App only speaks to it through the Controller API.
rem   call _controller.bat <project> <exe> <port> <title>
rem Nothing is started if something already answers on the port; otherwise the project is built, the Controller is started minimized, and this waits until it answers.
call "%~dp0_settings.bat"
cd /d "%MPAI_ROOT%"
powershell -NoProfile -Command "try{(New-Object Net.Sockets.TcpClient('127.0.0.1',%~3)).Close();exit 0}catch{exit 1}"
if not errorlevel 1 exit /b 0
dotnet build %1 -nologo -v q
if errorlevel 1 ( echo The Controller could not be built. & pause & exit /b 1 )
start %4 /min %2 --listen http://127.0.0.1:%~3
echo Waiting for the Controller...
powershell -NoProfile -Command "for($i=0;$i -lt 120;$i++){try{(New-Object Net.Sockets.TcpClient('127.0.0.1',%~3)).Close();exit 0}catch{Start-Sleep 1}};exit 1"
if errorlevel 1 ( echo The Controller does not answer. & pause & exit /b 1 )
exit /b 0
