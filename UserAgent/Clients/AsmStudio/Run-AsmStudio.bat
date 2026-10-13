@echo off
rem ASM Studio: builds (if needed) and starts it. Your sounds, scenes and names are kept in Documents\ASM Studio.
cd /d "%~dp0"
dotnet build AsmStudio.csproj -nologo -v q
if errorlevel 1 ( echo The build failed. & pause & exit /b 1 )
start "" "%~dp0bin\Debug\net10.0-windows10.0.19041.0\AsmStudio.exe"
