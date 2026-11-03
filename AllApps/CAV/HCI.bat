@echo off
rem Runs HCI (passenger conversation with the CAV) from the source (the first run builds it).
call "%~dp0..\_support\_settings.bat"
cd /d %MPAI_ROOT%
call "%~dp0..\_support\_controller.bat" Apps\CAV\HCIApps\Controller\HciController.csproj Apps\CAV\HCIApps\Controller\bin\Debug\net10.0\HciController.exe 5059 "HCI Controller"
dotnet run --project Apps\CAV\HCIApps\HciApp\src\HciApp.csproj
pause