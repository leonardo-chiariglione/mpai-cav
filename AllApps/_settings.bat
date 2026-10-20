@echo off
rem THE PLACE OF THE SOFTWARE AND OF THE SERVICE. The software is the folder above this one; the Service listens on port 5105.
for %%i in ("%~dp0..") do set MPAI_ROOT=%%~fi
set MPAI_MAS_SERVER=https://localhost:5105/
set MPAI_SERVICE_CONFIG=%MPAI_ROOT%\Apps\MAS\Service\mas-server-EI.json
