# A CONFERENCE IN THE MMM WORLD, with people who are not on this network:
#   MMM\Run-Conference.bat                      asks for the names of the participants (the convener first)
#   MMM\Run-Conference.ps1 -Names Leonardo,Anna,Bruno,Carla,Dario
# It starts the M-Instance with its world (the Metaverse Square, the MPAI building with its meeting room, a library and a cafe), closed with a MEETING CODE, and a Cloudflare quick
# tunnel that gives it a public https address (no account). The server stays closed to the network: only the tunnel leads to it, and nobody gets in without the code.
# The guests open the address, type their name (as it is in the list) and the code, allow the microphone, and walk to the MPAI Hall (W/S walk, A/D turn). Close both windows when
# the meeting is over: the address dies with them.
# The rights of the participants are for the meeting: -Until 19:00 (or -Hours 3, the default) is when it ends; they end with it, or when the convener ends it with the button.
param([string]$Names = '', [string]$Models = '', [string]$Until = '', [double]$Hours = 3)

$here = $PSScriptRoot
$port = 7099
$local = "http://127.0.0.1:$port"
if (-not $Names) { $Names = Read-Host 'Names of the participants, the convener first, separated by commas (for example Leonardo,Anna,Bruno,Carla,Dario)' }
$list = @($Names -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($list.Count -lt 1) { Write-Host 'No names.'; exit 1 }
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { Write-Host "Port $port is in use: a meeting server is already running. Close its window first."; exit 1 }

# An avatar (Rocketbox, MIT) for each: the files of Apps\ABV\AVS\Server\avatars, in turn.
if (-not $Models) {
    $files = 'Male03.glb', 'Thalia.glb', 'Persona1.glb', 'Male05.glb', 'Male08.glb'
    $Models = (0..($list.Count - 1) | ForEach-Object { "$($list[$_])=$($files[$_ % $files.Count])" }) -join ','
}
$code = Get-Random -Minimum 100000 -Maximum 999999
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

Write-Host ''
Write-Host "  MEETING CODE: $code" -ForegroundColor Yellow
Write-Host ''
$args1 = "run --project `"$here\Server\Mpai.Mmm.Server.csproj`" -c Release -- $local --world --code $code --invite $($list -join ',') --models $Models " + $(if ($Until) { "--until $Until" } else { "--hours $Hours" })
Start-Process -FilePath $dotnet -ArgumentList $args1 -WindowStyle Normal
Write-Host 'The server starts (the first time it compiles, about a minute)...'
$up = $false
for ($i = 0; $i -lt 180 -and -not $up; $i++) {
    Start-Sleep 2
    try { Invoke-WebRequest "$local/viewer/" -UseBasicParsing -TimeoutSec 3 | Out-Null; $up = $true }
    catch { if ($_.Exception.Response) { $up = $true } }     # 401: it answers, and asks for the code
}
if (-not $up) { Write-Host 'The server did not start. Look at its window.'; exit 1 }

$cf = @("$here\tools\cloudflared.exe", "$here\..\Apps\ABV\AVS\tools\cloudflared.exe", 'D:\DI\ABV\tools\cloudflared.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cf) { $cf = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source }
if (-not $cf) { Write-Host 'cloudflared.exe is not found (MMM\tools\, Apps\ABV\AVS\tools\, D:\DI\ABV\tools\). The meeting is open on this PC only:'; Write-Host "  $local/viewer/?world=1&mic=1&user=$($list[0])&code=$code"; Read-Host 'Enter to close' | Out-Null; exit 0 }
$log = Join-Path $env:TEMP 'mmm-tunnel.log'
Remove-Item $log -ErrorAction SilentlyContinue
Start-Process -FilePath $cf -ArgumentList 'tunnel', '--no-autoupdate', '--url', $local -RedirectStandardError $log -WindowStyle Minimized
$url = $null
for ($i = 0; $i -lt 40 -and -not $url; $i++) {
    Start-Sleep 1.5
    if (Test-Path $log) { $m = [regex]::Match((Get-Content $log -Raw -ErrorAction SilentlyContinue), 'https://[a-z0-9-]+\.trycloudflare\.com'); if ($m.Success) { $url = $m.Value } }
}
if (-not $url) { Write-Host 'The tunnel did not give an address. Look at the log:' $log; exit 1 }

Write-Host ''
Write-Host '  For the guests - send them this address and the code:' -ForegroundColor Green
Write-Host "    $url/viewer/?world=1&mic=1"
Write-Host "    meeting code: $code"
Write-Host "    names that are invited: $($list -join ', ')"
Write-Host "    the meeting ends: $(if ($Until) { 'at ' + $Until } else { 'in ' + $Hours + ' hours' }), or when you press the button (bottom left); the rights of everybody end with it"
Write-Host ''
Write-Host '  For you, on this PC (your name is already in it):' -ForegroundColor Green
Write-Host "    $local/viewer/?world=1&mic=1&user=$($list[0])&code=$code"
Write-Host ''
Write-Host '  Each guest: a PC with Chrome or Edge and a headset; type the name as listed, the code, allow the microphone;'
Write-Host '  W/S walk, A/D turn, to the MPAI doors on the right; the door to it opens by itself for the invited. In the meeting room you sit down; Q stands up, E sits.'
Start-Process "$local/viewer/?world=1&mic=1&user=$($list[0])&code=$code"
Read-Host 'Enter to stop the tunnel (the server has its own window)' | Out-Null
Get-Process cloudflared -ErrorAction SilentlyContinue | Stop-Process -Force
