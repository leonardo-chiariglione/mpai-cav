@echo off
rem COMPANY PERFORMANCE PREDICTION (MPAI-CUI CPP V2.1), console. Runs the Controller as a program of its own and the User Agent against it, for the company file given:
rem     Cui.bat Samples\distressed.json [--horizon 24] [--json result.json]
rem Build once:  dotnet build Controller\CuiController.csproj  and  dotnet build Ua\CuiUa.csproj
set HERE=%~dp0
set PORT=5061
start "CuiController" /min dotnet "%HERE%Controller\bin\Debug\net10.0\CuiController.dll" --listen http://127.0.0.1:%PORT% --model "%HERE%..\..\..\AIMs\CUI\V2.1\Models\FinancialDefault-Polish5y.json"
timeout /t 8 /nobreak >nul
dotnet "%HERE%Ua\bin\Debug\net10.0\CuiUa.dll" --company %* --controller http://127.0.0.1:%PORT%
taskkill /fi "WINDOWTITLE eq CuiController*" /f >nul 2>&1
