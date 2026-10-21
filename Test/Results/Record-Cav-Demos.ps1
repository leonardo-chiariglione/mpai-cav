# Re-records the four older CAV use cases with the current CAV, started by the Windows task "MPAI CAV recordings 2026-10-16" at 03:00.
# Each goes into Demos\<name> of the tree (the old one is replaced; the 8 October recordings stay in Downloads\highway-demo) and a copy to Downloads\highway-demo\<name>-2026-10-16.
$tree = Split-Path -Parent (Split-Path -Parent $PSScriptRoot); $demos = Join-Path $tree 'Demos'; $down = Join-Path $env:USERPROFILE 'Downloads\highway-demo'; $log = Join-Path $PSScriptRoot 'cav-recordings.txt'
Set-Location (Join-Path $tree 'Test\Mpai.Aif.Tests')
$runs = @( @('traffic','1',90), @('truck','slow',90), @('behind','behind',90), @('overtake','overtake',100) )
foreach ($r in $runs) {
    $name = $r[0]; $dest = Join-Path $demos $name
    if (Test-Path $dest) { Get-ChildItem $dest -Recurse -File | ForEach-Object { $_.Delete() } }
    $env:MPAI_HIGHWAY_LIVE = $r[1]; $env:MPAI_HIGHWAY_SECONDS = "$($r[2])"; $env:MPAI_HIGHWAY_RECORD = $dest
    $res = dotnet test --filter 'FullyQualifiedName~TheCavOnTheHighwayLive' 2>&1 | Select-String -Pattern 'Passed!|Failed!' | Select-Object -First 1
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm') $name : $res" | Add-Content $log
    if (Test-Path "$dest\index.html") { Copy-Item $dest "$down\$name-2026-10-16" -Recurse -Force }
}
"$(Get-Date -Format 'yyyy-MM-dd HH:mm') done" | Add-Content $log
