# Runs the Slow group of the tests of D:\EI at night (started by the scheduled task "EI Slow tests 22:00") and writes what it says to C:\Users\Thor\Downloads\slow-<date>.txt
$ErrorActionPreference = 'Continue'
$out = "C:\Users\Thor\Downloads\slow-$(Get-Date -Format 'yyyy-MM-dd').txt"
"Started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Set-Content $out -Encoding utf8
Set-Location D:\EI\Test\Mpai.Aif.Tests
& dotnet test --no-build --filter "Group=Slow" --logger "console;verbosity=normal" 2>&1 | Out-File $out -Append -Encoding utf8
"Finished $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Add-Content $out -Encoding utf8