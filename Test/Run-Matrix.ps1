<#
.SYNOPSIS
  The test matrix of M3207: builds the entry projects, runs the tests, and reports
  what blocks a step apart from what only informs.

.DESCRIPTION
  Blocking: the builds of the AIF and the tests, and every test with Blocks=Yes
  (the Metadata, Loops, CAV status and Model hashes tests). If any of these fails,
  the step may not be committed, and the script exits with 1.

  Informative: the builds of MAS-App and every test with Blocks=No (the Workflows
  test; with -Full, also the Service tests). Their results are reported and a
  failure is recorded, but it does not stop the step (M3206 Section 2).

.PARAMETER Quick
  Only the Fast group: the Metadata, Loops, CAV status and Workflows tests, in
  seconds. Leaves out the Model hashes (about 30 s) and the Service tests.

.PARAMETER Full
  Also the Service tests of MAS-App (a few minutes, mostly loading the models),
  and the checklist of the Apps by voice. For the end of a phase.
#>
param([switch]$Quick, [switch]$Full)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$results = Join-Path $env:TEMP 'mpai-matrix'
New-Item -ItemType Directory -Force $results | Out-Null

$blockingBuilds = @(
    'Test\Mpai.Aif.Tests\Mpai.Aif.Tests.csproj',
    'Test\M3194Test.csproj'
)
$informativeBuilds = @(
    'MPAIApps\MmcApps\AmqServer\src\AmqServer.csproj',
    'MPAIApps\RcaApp\src\RcaApp.csproj',
    'MPAIApps\RcaWeb\Host\RcaWeb.Host.csproj',
    'MPAIApps\StoreService\StoreService.csproj',
    'MPAIApps\StoreApp\StoreApp.csproj'
)
# The programs in legacy (and Test\legacy) are not built: MAS-App replaced them
# (M3209). legacy\RcaConsole does not build (DeviceRegistry.Acquire changed under it).

$blocking    = [System.Collections.Generic.List[string]]::new()
$informative = [System.Collections.Generic.List[string]]::new()
$failedBlocking = $false

function Build([string]$project) {
    $out = & dotnet build (Join-Path $root $project) -nologo -v q 2>&1
    $errors = ($out | Select-String -Pattern '(\d+) Error\(s\)' | Select-Object -Last 1).Matches.Groups[1].Value
    return ($LASTEXITCODE -eq 0 -and $errors -eq '0')
}

Write-Host "Building..." -ForegroundColor Cyan
foreach ($p in $blockingBuilds) {
    if (Build $p) { $blocking.Add("PASS  build  $p") } else { $blocking.Add("FAIL  build  $p"); $failedBlocking = $true }
}
foreach ($p in $informativeBuilds) {
    if (Build $p) { $informative.Add("PASS  build  $p") } else { $informative.Add("FAIL  build  $p") }
}

# Runs the tests a filter selects and returns one line per test, from the .trx report.
function RunTests([string]$filter, [string]$name) {
    $trx = Join-Path $results "$name.trx"
    if (Test-Path $trx) { Remove-Item $trx -Confirm:$false }
    $project = Join-Path $root 'Test\Mpai.Aif.Tests\Mpai.Aif.Tests.csproj'
    & dotnet test $project --no-build -nologo --filter $filter --logger "trx;LogFileName=$trx" 2>&1 | Out-Null
    $lines = @()
    if (-not (Test-Path $trx)) { return ,@("FAIL  tests  ($name) no results were produced") }
    [xml]$x = Get-Content $trx -Raw
    foreach ($r in $x.TestRun.Results.UnitTestResult) {
        $outcome = switch ($r.outcome) { 'Passed' { 'PASS' } 'Failed' { 'FAIL' } 'NotExecuted' { 'SKIP' } default { $r.outcome } }
        $test = $r.testName -replace '^Mpai\.Aif\.Tests\.', ''
        $line = "{0}  test   {1}  ({2:N1} s)" -f $outcome, $test, ([TimeSpan]::Parse($r.duration).TotalSeconds)
        $message = $r.Output.ErrorInfo.Message
        if ($outcome -eq 'SKIP' -and $r.Output.StdOut) { $message = $r.Output.StdOut }
        if ($message) { $line += "`n        " + (($message -split "`n" | Select-Object -First 12) -join "`n        ") }
        $lines += $line
    }
    return ,$lines
}

Write-Host "Testing..." -ForegroundColor Cyan
$blockingFilter    = if ($Quick) { 'Blocks=Yes&Group=Fast' } else { 'Blocks=Yes' }
$informativeFilter = if ($Full)  { 'Blocks=No' } else { 'Blocks=No&Group!=Service' }

foreach ($l in (RunTests $blockingFilter 'blocking'))       { $blocking.Add($l);    if ($l.StartsWith('FAIL')) { $failedBlocking = $true } }
foreach ($l in (RunTests $informativeFilter 'informative')) { $informative.Add($l) }

Write-Host ""
Write-Host "BLOCKING - the AIF and the CAV: these decide whether the step may be committed" -ForegroundColor Yellow
$blocking    | ForEach-Object { Write-Host "  $_" -ForegroundColor ($(if ($_.StartsWith('FAIL')) { 'Red' } else { 'Gray' })) }
Write-Host ""
Write-Host "INFORMATIVE - MAS-App: reported and recorded, not a reason to stop" -ForegroundColor Yellow
$informative | ForEach-Object { Write-Host "  $_" -ForegroundColor ($(if ($_.StartsWith('FAIL')) { 'DarkYellow' } else { 'Gray' })) }

if ($Full) {
    Write-Host ""
    Write-Host "THE APPS BY VOICE (M3207 3.6) - to be checked by the author, in the browser and on the desktop:" -ForegroundColor Yellow
    @(
        'MPAI-MAS  welcome spoken; the list of Apps shown; an App chosen by voice starts it; Stop returns to the list',
        'MAD       two spoken turns, the second referring to the first; typing instead of speaking works',
        'AMQ       "yes"; a picture chosen; a spoken question answered about it; "no" ends',
        'MAT       a pair of languages chosen; a spoken sentence said in the other language, and shown',
        'MPD       the camera allowed; a happy sentence answered with a smile',
        'Both      the same checks give the same result; Stop during any step returns to the list'
    ) | ForEach-Object { Write-Host "  [ ] $_" }
}

Write-Host ""
if ($failedBlocking) {
    Write-Host "RESULT: a blocking build or test failed - the step may not be committed." -ForegroundColor Red
    exit 1
}
Write-Host "RESULT: every blocking build and test passed." -ForegroundColor Green
exit 0
