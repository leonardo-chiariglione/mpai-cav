# CLOSES AN MMM MEETING: the meeting server (MMM meeting, MMM group meeting, MMM world), its public address (the Cloudflare tunnel of MMM\tools), the window that printed the links, and any
# Whisper left by a server that has ended. Kept: Ollama (the Secretary's language model, shared with other Apps) and the screen helper in the tray (Quit from its icon).
$all = @(Get-CimInstance Win32_Process)
$alive = @{}; foreach ($p in $all) { $alive[[int]$p.ProcessId] = $true }
$stop = @()
$stop += $all | Where-Object { $_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'Mpai\.Mmm\.Server' }                                         # the server (dotnet run and the server itself)
$stop += $all | Where-Object { $_.Name -eq 'cloudflared.exe' -and $_.CommandLine -match '\\MMM\\tools\\cloudflared' }                              # the public address
$stop += $all | Where-Object { $_.Name -eq 'powershell.exe' -and $_.CommandLine -match 'Run-(Meeting|Conference)\.ps1' }                         # the window with the links
$stop += $all | Where-Object { $_.Name -eq 'whisper-server.exe' -and -not $alive[[int]$_.ParentProcessId] }                                     # a Whisper whose server has ended
$stop = @($stop | Sort-Object ProcessId -Unique)
if ($stop.Count -eq 0) { Write-Host 'No MMM meeting is running.'; exit 0 }
foreach ($p in $stop) {
    $what = switch ($p.Name) { 'cloudflared.exe' { 'the public address' } 'whisper-server.exe' { 'a leftover Whisper' } 'powershell.exe' { 'the window with the links' } default { 'the meeting server' } }
    try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; Write-Host "Stopped $what ($($p.ProcessId))." } catch { Write-Host "Could not stop $what ($($p.ProcessId)): $($_.Exception.Message)" }
}
