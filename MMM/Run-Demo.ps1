# THE MMM DEMONSTRATION OF USE CASE 2 ("Friends meet in the metaverse"), one command:
#   MMM\Run-Demo.ps1            the presenter steps through it: Space, right arrow or a click
#   MMM\Run-Demo.ps1 4          it plays by itself, 4 seconds a step
# Starts the M-Instance and its viewer, opens the viewer in the default browser, and stays
# in this window (Enter here also shows the next step). Ctrl+C stops it.
param([string]$Pace = 'manual')

$url = 'http://127.0.0.1:7099'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (Get-NetTCPConnection -LocalPort 7099 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host 'Port 7099 is in use: a demonstration is already running. Stop it first (Ctrl+C in its window).'; exit 1
}
Start-Job { Start-Sleep 8; Start-Process $using:url/viewer/ } | Out-Null
& $dotnet run --project (Join-Path $PSScriptRoot 'Server\Mpai.Mmm.Server.csproj') -- $url --demo $Pace
