# THE BROWSER SERVER, in its own window: what it says is shown and kept in logs\host.log, beside this file
# (host-<time>.log if an older window still holds host.log).
$logs = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$Host.UI.RawUI.WindowTitle = 'Thalia browser server'
$log = Join-Path $logs 'host.log'
try { [IO.File]::Open($log, 'OpenOrCreate', 'ReadWrite', 'None').Close() } catch { $log = Join-Path $logs ('host-' + (Get-Date -Format 'HHmmss') + '.log') }
Write-Host "THALIA BROWSER SERVER - https://localhost:5010/ - close this window to stop it" -ForegroundColor Cyan
if ((Get-NetTCPConnection -State Listen -LocalPort 5010 -ErrorAction SilentlyContinue)) {
    Write-Host "Another Thalia browser server is still running on port 5010: close its window first, then run this again." -ForegroundColor Yellow
    return
}
Set-Location $env:MPAI_ROOT
dotnet run --project AIF\UserAgent\Clients\Browser\Host\RcaWeb.Host.csproj -- --Service $env:MPAI_MAS_SERVER --Urls https://localhost:5010 2>&1 | Tee-Object -FilePath $log
