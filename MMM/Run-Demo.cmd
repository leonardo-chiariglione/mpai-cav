@echo off
rem Starts the MMM demonstration of Use Case 2 (see Run-Demo.ps1). Windows does not let PowerShell
rem run scripts by default; this lets it run this one script, for this one run, and changes nothing.
rem   Run-Demo.cmd          the presenter steps through it (Space, right arrow, a click)
rem   Run-Demo.cmd 4        it plays by itself, 4 seconds a step
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Demo.ps1" %*
