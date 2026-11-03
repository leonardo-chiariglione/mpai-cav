@echo off
rem ASM BY VOICE (audio scene management, standalone): you speak or type to an audio scene and it changes; the avatar answers.
rem Two programs: the Controller (AsmController) and the User Agent (the window, AsmUa). The window tells the Controller to start the Module; the Controller starts it and its AIMs. This launcher starts
rem the Controller first (in a window of its own, minimized) unless one already answers, then the window. Starts the language model (Ollama) if it is not running, and builds what is not built.
rem If the window cannot be rebuilt because an earlier one is still open, the latest build kept apart (bin\Drive) is started instead.
call "%~dp0..\_support\_settings.bat"
call "%~dp0..\_support\_ollama.bat"
cd /d "%MPAI_ROOT%"
set ASM_CONTROLLER=http://127.0.0.1:5057
powershell -NoProfile -Command "try{(New-Object Net.Sockets.TcpClient('127.0.0.1',5057)).Close();exit 0}catch{exit 1}"
if not errorlevel 1 goto window
dotnet build Apps\CAE\ASM\Controller\AsmController.csproj -nologo -v q
if errorlevel 1 ( echo The Controller could not be built. & pause & exit /b 1 )
start "ASM Controller" /min "Apps\CAE\ASM\Controller\bin\Debug\net10.0\AsmController.exe" --listen %ASM_CONTROLLER%
echo Waiting for the Controller...
powershell -NoProfile -Command "for($i=0;$i -lt 120;$i++){try{(New-Object Net.Sockets.TcpClient('127.0.0.1',5057)).Close();exit 0}catch{Start-Sleep 1}};exit 1"
if errorlevel 1 ( echo The Controller does not answer. & pause & exit /b 1 )
:window
dotnet build Apps\CAE\ASM\Ua\AsmUa.csproj -nologo -v q
if errorlevel 1 goto apart
start "" "Apps\CAE\ASM\Ua\bin\Debug\net10.0-windows10.0.19041.0\AsmUa.exe" --controller %ASM_CONTROLLER%
exit /b 0
:apart
echo The program could not be rebuilt: close every ASM window and start this again. Starting the latest build kept apart...
if not exist "Apps\CAE\ASM\Ua\bin\Drive\AsmUa.exe" ( echo There is none. & pause & exit /b 1 )
start "" "Apps\CAE\ASM\Ua\bin\Drive\AsmUa.exe" --controller %ASM_CONTROLLER%
