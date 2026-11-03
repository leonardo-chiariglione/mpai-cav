@echo off
setlocal
set HERE=%~dp0
set SRC=%HERE%src\AmcApp.csproj
echo ============================================================
echo   Building AmcApp
echo ============================================================
taskkill /IM AmcApp.exe /F >nul 2>&1
dotnet build "%SRC%" -c Release
if errorlevel 1 ( echo BUILD FAILED. & pause & exit /b 1 )
echo.
echo DONE:  %HERE%src\bin\Release\net10.0-windows10.0.19041.0\AmcApp.exe
pause
