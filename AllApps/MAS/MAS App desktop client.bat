@echo off
rem THE MAS APP, the MAS launcher: the Service, then the Desktop client. Choose an App in its window.
call "%~dp0..\_support\_settings.bat"
call "%~dp0..\_support\_service.bat"
cd /d %MPAI_ROOT%
start "MAS App" dotnet run --project AIF\UserAgent\Clients\Desktop\src\RcaApp.csproj