@echo off
rem OPEN TO THE NETWORK: the same server, answering on every address of this PC (ports 5180 and 5181). Use it only while the others are in the meeting, and close it after.
rem The Windows Firewall must allow the ports (a rule you add and REMOVE yourself):
rem   add:    New-NetFirewallRule -DisplayName "ABV demo" -Direction Inbound -Protocol TCP -LocalPort 5180,5181 -Action Allow
rem   remove: Remove-NetFirewallRule -DisplayName "ABV demo"
set ABV_LAN=1
cd /d "%~dp0Server"
dotnet run -c Release
