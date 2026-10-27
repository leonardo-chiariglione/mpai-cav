@echo off
rem ASM BY VOICE (audio scene management, standalone): you speak or type to an audio scene and it changes; the avatar answers. Starts the language model (Ollama) if it is not running, builds the program the first time, and opens its window.
call "%~dp0..\_support\_settings.bat"
call "%~dp0..\_support\_ollama.bat"
cd /d "%MPAI_ROOT%"
dotnet build Apps\CAE\ASM\Ua\AsmUa.csproj -nologo -v q
if errorlevel 1 ( echo The build failed. & pause & exit /b 1 )
start "" "Apps\CAE\ASM\Ua\bin\Debug\net10.0-windows10.0.19041.0\AsmUa.exe"
