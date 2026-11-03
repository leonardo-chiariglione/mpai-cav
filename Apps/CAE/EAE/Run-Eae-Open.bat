@echo off
rem OPEN TO THE NETWORK: the same server, answering on every address of this PC (ports 5190 and 5191). Use it only while the others are in the conference, and close it after.
rem The Windows Firewall must allow the ports (a rule you add and REMOVE yourself):
rem   add:    New-NetFirewallRule -DisplayName "EAE demo" -Direction Inbound -Protocol TCP -LocalPort 5190,5191 -Action Allow
rem   remove: Remove-NetFirewallRule -DisplayName "EAE demo"
set EAE_LAN=1
cd /d "%~dp0Server"
dotnet run -c Release
