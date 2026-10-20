# The whole test suite, started by the Windows task "MPAI full tests 2026-10-15" at 23:00. Results: D:\DI\Test\Results\2026-10-15\
$out = 'D:\DI\Test\Results\2026-10-15'
New-Item -ItemType Directory -Force $out | Out-Null
Set-Location 'D:\DI\Test\Mpai.Aif.Tests'
$start = Get-Date
dotnet test --logger "trx;LogFileName=all.trx" --results-directory $out *> "$out\console.txt"
$code = $LASTEXITCODE
"started $start; ended $(Get-Date); exit code $code" | Set-Content "$out\done.txt"
