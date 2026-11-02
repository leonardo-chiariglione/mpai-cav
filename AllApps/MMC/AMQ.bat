@echo off
rem Runs AMQ (picture questions, standalone) from the source (the first run builds it).
call "%~dp0..\_support\_settings.bat"
cd /d %MPAI_ROOT%
call "%~dp0..\_support\_controller.bat" Apps\MMC\Controller\MmcController.csproj Apps\MMC\Controller\bin\Debug\net10.0\MmcController.exe 5058 "MMC Controller"
dotnet run --project Apps\MMC\AMQ\Standalone\App\src\AmqApp.csproj
pause