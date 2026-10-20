@echo off
rem AUDIO SCENE MANAGEMENT, the App: it builds the scene you ask for from sounds and speech; it starts the Controller of the Module and the AIM host of its AIMs itself and ends them with its window.
call "%~dp0_settings.bat"
cd /d "%MPAI_ROOT%"
dotnet build Apps\CAE\ASM\App\AsmApp.csproj -nologo -v q
if errorlevel 1 ( echo The build failed. & pause & exit /b 1 )
start "" "Apps\CAE\ASM\App\bin\Debug\net10.0-windows10.0.19041.0\AsmApp.exe"
