@echo off
rem ASM STUDIO, with the parts of Audio Scene Management as they are: the Module's AIMs on an AIM host, the Controller, and the Studio (the User Agent).
rem Close the Studio's window to end all three. What the AIM host and the Controller say is kept in logs\asm-aimhost.log and logs\asm-controller.log.
call "%~dp0_settings.bat"
powershell -NoProfile -ExecutionPolicy Bypass -File "%MPAI_ROOT%\Apps\CAE\ASM\Start-Asm.ps1" -Root "%MPAI_ROOT%" -Logs "%~dp0logs"
if errorlevel 1 pause
