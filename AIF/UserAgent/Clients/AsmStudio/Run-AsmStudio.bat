@echo off
rem ASM Studio is a User Agent: it needs its Controller, and the Controller its Module. This starts all three (Apps\CAE\ASM\Start-Asm.ps1).
rem Your sounds, scenes and names are kept in Documents\ASM Studio.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\..\..\..\Apps\CAE\ASM\Start-Asm.ps1"
if errorlevel 1 pause
