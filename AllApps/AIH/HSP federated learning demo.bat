@echo off
rem AIH-HSP V1.1, FEDERATED LEARNING, NARRATED (a simulation): the Back End uploads an AIM with a watermarked model, three End Users train on their own (synthetic) images,
rem the Back End checks, averages, validates, licenses and audits; a second round with a new watermark; ends with the table of known problems (built / simulated / planned).
call "%~dp0..\_support\_settings.bat"
cd /d "%MPAI_ROOT%"
dotnet build Apps\AIH\HSP\FlDemo\FlDemo.csproj -nologo -v q
if errorlevel 1 ( echo The build failed. & pause & exit /b 1 )
dotnet Apps\AIH\HSP\FlDemo\bin\Debug\net10.0\AihFlDemo.dll
pause
