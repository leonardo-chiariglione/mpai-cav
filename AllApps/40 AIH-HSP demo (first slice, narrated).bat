@echo off
rem AIH-HSP V1.1, THE FIRST SLICE, NARRATED: two End Users with their Health Front Ends, the Health Back End (the Trust Authority) with a test Blockchain, and a Third-Party User.
rem Each Front End and the Back End is a Controller with its Module, and the Module's AIMs run on an AIM host of their own.
call "%~dp0_settings.bat"
cd /d "%MPAI_ROOT%"
dotnet build Apps\AIH\HSP\Demo\AihDemo.csproj -nologo -v q
if errorlevel 1 ( echo The build failed. & pause & exit /b 1 )
dotnet Apps\AIH\HSP\Demo\bin\Debug\net10.0\AihDemo.dll
pause
