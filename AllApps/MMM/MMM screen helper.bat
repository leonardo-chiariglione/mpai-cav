@echo off
rem Starts the MMM screen helper in the tray (the program that shares a window in an MMM meeting without the browser's "Stop sharing" bar). Run-Meeting starts it by itself; this is for the other cases.
cd /d "%~dp0..\..\MMM\ScreenHelper"
if not exist "bin\Release\net10.0-windows\Mpai.Mmm.ScreenHelper.dll" dotnet build -c Release -v q
start "" /min dotnet "bin\Release\net10.0-windows\Mpai.Mmm.ScreenHelper.dll"
