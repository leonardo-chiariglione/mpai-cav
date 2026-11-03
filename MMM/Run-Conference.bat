@echo off
rem A conference in the MMM world, for people outside this network (see Run-Conference.ps1). Runs the script without needing the PowerShell script policy changed.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Conference.ps1" %*
