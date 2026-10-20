# THE CAV, to run or to watch. Choose a use case: it runs in real time and its animation is shown in the browser at http://127.0.0.1:7100/ ;
# or open the animations of runs recorded earlier (Demos). Each run takes a few minutes; Ctrl+C stops it.
$root  = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))   # Apps\CAV\CAO -> the repository
$cases = @(
    @{ Key = '1'; Mode = '1';                Seconds = 90; Name = 'Busy two-lane traffic (cars and trucks change lanes around the CAV)' },
    @{ Key = '2'; Mode = 'slow';             Seconds = 90; Name = 'A slow truck 150 m ahead: the CAV slows and follows' },
    @{ Key = '3'; Mode = 'behind';           Seconds = 90; Name = 'A slow truck ahead, fast cars coming up behind (the CAV stays in its lane)' },
    @{ Key = '4'; Mode = 'overtake';         Seconds = 100; Name = 'Overtaking the truck: the CAV waits for the fast cars, pulls out, passes and returns' },
    @{ Key = '5'; Mode = 'junction-route';   Seconds = 70; Name = 'Junction: an oncoming car turns left across the CAV - it tells the CAV its route' },
    @{ Key = '6'; Mode = 'junction-lights';  Seconds = 70; Name = 'Junction: the same turn - the CAV sees the indicator in the picture' },
    @{ Key = '7'; Mode = 'junction-nothing'; Seconds = 70; Name = 'Junction: the same turn - the CAV is told nothing' },
    @{ Key = '8'; Mode = 'junction-overtake'; Seconds = 75; Name = 'Junction: the car ahead slows and signals right - the CAV reads it, sees the other lane empty, and passes' }
)
Write-Host ''
Write-Host '  THE CAV' -ForegroundColor Cyan
Write-Host '  A   WATCH THE ANIMATIONS of the recorded runs (opens in your browser, nothing runs)' -ForegroundColor Green
Write-Host '  Or run a use case in real time (it takes a few minutes; the animation opens as it runs):'
foreach ($c in $cases) { Write-Host ('    {0}  {1}' -f $c.Key, $c.Name) }
Write-Host '  Q   quit'
Write-Host ''
$pick = (Read-Host '  Your choice').Trim().ToUpper()
if ($pick -eq 'A') { Start-Process (Join-Path $root 'Demos\index.html'); return }
$case = $cases | Where-Object { $_.Key -eq $pick }
if (-not $case) { return }
if (-not (Test-Path (Join-Path $root 'Models\yolox_s.onnx'))) { Write-Host '  Models\yolox_s.onnx is absent: the model files are obtained separately.' -ForegroundColor Yellow; return }
$env:MPAI_HIGHWAY_LIVE = $case.Mode; $env:MPAI_HIGHWAY_SECONDS = "$($case.Seconds)"; $env:MPAI_HIGHWAY_HOLD = '120'
Write-Host ('  Running: ' + $case.Name)
Write-Host '  The animation opens in your browser in a few seconds (http://127.0.0.1:7100/). It stays 2 minutes after the run ends.'
Start-Job { Start-Sleep 25; Start-Process 'http://127.0.0.1:7100/' } | Out-Null
Set-Location (Join-Path $root 'Test\Mpai.Aif.Tests')
dotnet test --filter 'FullyQualifiedName~TheCavOnTheHighwayLive' 2>&1 | Select-String -Pattern 'Passed!|Failed!|error' 

