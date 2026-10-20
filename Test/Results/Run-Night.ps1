# THE NIGHT RUN: the long tests, in the order of what matters most, each with its own log.
# Run it when nobody is working: it keeps the processor busy for about an hour and a half.
#   powershell -ExecutionPolicy Bypass -File D:\DI\Test\Results\Run-Night.ps1
# Logs and a summary go to Test\Reports\Night\<date>\ (git-ignored).
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $root ('Test\Reports\Night\' + (Get-Date -Format 'yyyy-MM-dd'))
New-Item -ItemType Directory -Force $out | Out-Null
$project = Join-Path $root 'Test\Mpai.Aif.Tests\Mpai.Aif.Tests.csproj'
$summary = Join-Path $out 'summary.txt'
"Night run started $(Get-Date -Format 's')" | Set-Content $summary

dotnet build $project -nologo -v q *> (Join-Path $out '0-build.log')
"build exit code $LASTEXITCODE" | Add-Content $summary
if ($LASTEXITCODE -ne 0) { 'The build failed: no test was run.' | Add-Content $summary; exit 1 }

$groups = [ordered]@{
    '1-overtaking'  = 'FullyQualifiedName~HighwayOvertakingTests|FullyQualifiedName~HighwayOncomingTests'
    '2-conformance' = 'FullyQualifiedName~MetadataTests|FullyQualifiedName~EssStage1Tests|FullyQualifiedName~EssSpecificationTests|FullyQualifiedName~CavSchemaTests|FullyQualifiedName~StoreTests|FullyQualifiedName~HighwayTests'
    '3-ams-and-cav' = 'FullyQualifiedName~AmsStage1Tests|FullyQualifiedName~CavStage1Tests|FullyQualifiedName~MasStage1Tests|FullyQualifiedName~SyntheticDriveTests|FullyQualifiedName~AsmTranslationTests|FullyQualifiedName~VoiceLanguageTests|FullyQualifiedName~AsmVoiceChainTests'
    '4-dialogue'    = 'FullyQualifiedName~CaoTests'
}
foreach ($name in $groups.Keys) {
    $log = Join-Path $out "$name.log"
    $began = Get-Date
    dotnet test $project --no-build --filter $groups[$name] *> $log
    $minutes = [math]::Round(((Get-Date) - $began).TotalMinutes, 1)
    $result = (Select-String -Path $log -Pattern '^(Passed!|Failed!)' | Select-Object -Last 1).Line
    $failed = (Select-String -Path $log -Pattern '^\s+Failed ' | ForEach-Object { $_.Line.Trim() }) -join "`n    "
    "$name  [$minutes min]  $result" | Add-Content $summary
    if ($failed) { "    $failed" | Add-Content $summary }
}
"Night run finished $(Get-Date -Format 's')" | Add-Content $summary

