# SPEECH RESTORATION SYSTEM, A DEMONSTRATION IN ONE CLICK: a real recording (LibriSpeech, speaker 1462) is damaged in two places (made silent at 1.0-1.8 s and 3.0-3.6 s); the Module makes a model of the voice
# of ANOTHER recording (speaker 1272) and speaks "hello there" and "good morning" with it into the two gaps (the Piper Text To Speech with the OpenVoice tone colour of the model). It then plays, one after the other,
# the original, the damaged and the restored recording, and opens the folder with the three files.
#   _srs-demo.ps1 [-NoPlay]
param([switch]$NoPlay)
$ErrorActionPreference = 'Stop'
$root = $env:MPAI_ROOT; if (-not $root) { $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$voice = 'D:\AI\Datasets\libri_wav\1272\clip.wav'; $original = 'D:\AI\Datasets\libri_wav\1462\clip.wav'
foreach ($f in $voice, $original) { if (-not (Test-Path $f)) { Write-Host "The recording $f is not on this PC: the demonstration needs the two LibriSpeech clips of D:\AI\Datasets\libri_wav." -ForegroundColor Red; exit 1 } }
$project = Join-Path $root 'Apps\CAE\SRS\App\SrsApp.csproj'; $exe = Join-Path $root 'Apps\CAE\SRS\App\bin\Debug\net10.0\SrsApp.exe'
$out = Join-Path (Join-Path $env:USERPROFILE 'Downloads') "MPAI USC\SRS demo\$(Get-Date -Format 'yyyy-MM-dd HH.mm.ss')"
Write-Host 'Building the Speech Restoration System (a first build takes a while)...' -ForegroundColor Cyan
& dotnet build $project -nologo -v q 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host 'The build failed.' -ForegroundColor Red; exit 1 }
& $exe --demo $out --original $original
Write-Host "`nRestoring: the model of the voice, the speech for the two gaps, the assembly (about a minute)..." -ForegroundColor Cyan
& $exe --voice $voice --damaged (Join-Path $out 'damaged.wav') --sections '1.0-1.8,3.0-3.6' --say 'hello there' --say 'good morning' --out (Join-Path $out 'restored.wav')
if ($LASTEXITCODE -ne 0) { Write-Host 'The restoration stopped: see what it said above.' -ForegroundColor Yellow; exit 1 }
if (-not $NoPlay) {
    foreach ($pair in @(@('original', 'The ORIGINAL recording'), @('damaged', 'The DAMAGED recording: two gaps'), @('restored', 'The RESTORED recording: the gaps spoken in the voice of another speaker'))) {
        Write-Host $pair[1] -ForegroundColor Green
        (New-Object Media.SoundPlayer (Join-Path $out ($pair[0] + '.wav'))).PlaySync()
        Start-Sleep -Milliseconds 700
    }
}
Write-Host "`nThe three files are in $out" -ForegroundColor Green
Invoke-Item $out