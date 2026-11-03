@echo off
setlocal
echo Building HciApp.exe...
rem This script's own folder, without the trailing backslash.
set HERE=%~dp0
set HERE=%HERE:~0,-1%
set SRC=%HERE%\src\HciApp.csproj
set TMP=%HERE%\_build
taskkill /IM HciApp.exe /F >nul 2>&1
dotnet publish "%SRC%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o "%TMP%"
if errorlevel 1 ( echo BUILD FAILED. & pause & exit /b 1 )
copy /Y "%TMP%\HciApp.exe" "%HERE%\" >nul
if exist "%TMP%\WebView2Loader.dll" copy /Y "%TMP%\WebView2Loader.dll" "%HERE%\" >nul
rmdir /S /Q "%TMP%" 2>nul
echo DONE: %HERE%\HciApp.exe
