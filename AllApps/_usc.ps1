# THE APPS OF CAE-USC, picked with dialogs: you choose the files and say the few words each App needs; the App is built if it is not, run, and the folder of its results is opened.
#   _usc.ps1 -App SRS | EES | ARP | EAE
param([Parameter(Mandatory)][ValidateSet('SRS', 'EES', 'ARP', 'EAE')][string]$App)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic
$root = $env:MPAI_ROOT; if (-not $root) { $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }

function Pick([string]$title, [string]$filter = 'All files (*.*)|*.*', [bool]$multi = $false) {
    $d = New-Object Windows.Forms.OpenFileDialog; $d.Title = $title; $d.Filter = $filter; $d.Multiselect = $multi
    if ($d.ShowDialog() -ne 'OK') { throw 'Cancelled.' }
    if ($multi) { $d.FileNames } else { $d.FileName }
}
function Ask([string]$prompt, [string]$title, [string]$default = '') {
    $a = [Microsoft.VisualBasic.Interaction]::InputBox($prompt, $title, $default)
    if ($a -eq '') { throw 'Cancelled.' }
    $a
}
function Yes([string]$question, [string]$title) { [Windows.Forms.MessageBox]::Show($question, $title, 'YesNo', 'Question') -eq 'Yes' }

$names = @{ SRS = 'Speech Restoration System'; EES = 'Emotion Enhanced Speech'; ARP = 'Audio Recording Preservation'; EAE = 'Enhanced Audioconference Experience' }
$title = $names[$App]
$project = Join-Path $root "Apps\CAE\$App\App\$((Get-Culture).TextInfo.ToTitleCase($App.ToLower()))App.csproj"
$exe = Join-Path $root "Apps\CAE\$App\App\bin\Debug\net10.0\$((Get-Culture).TextInfo.ToTitleCase($App.ToLower()))App.exe"
$out = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "MPAI USC\$App\$(Get-Date -Format 'yyyy-MM-dd HH.mm.ss')"
New-Item -ItemType Directory -Force $out | Out-Null
$wav = 'WAV files (*.wav)|*.wav|All files (*.*)|*.*'

try {
    switch ($App) {
        'SRS' {
            $voice = Pick 'Speech of the speaker, to make the model of the voice from (one or more files)' $wav $true
            $damaged = Pick 'The recording that is damaged' $wav
            $sections = Ask "Where is it damaged, in seconds from the start of the recording?`n`nWrite start-end, sections apart by commas: 0.5-1.0,2.0-2.5" $title '0.5-1.0'
            $says = (Ask "What was said in each damaged section, in order?`n`nOne text for each section, apart by |" $title 'hello') -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
            $arguments = @('--voice') + $voice + @('--damaged', $damaged, '--sections', $sections, '--out', (Join-Path $out 'restored.wav'))
            foreach ($t in $says) { $arguments += @('--say', $t) }
            if (-not (Yes 'Speak in the voice of the speaker?`n`nYes: the voice engine makes the voice (it takes longer).`nNo: a stand-in tone, to test.' $title)) { $arguments += '--stand-in' }
        }
        'EES' {
            $speech = Pick 'The emotionless speech' $wav
            $arguments = @('--speech', $speech, '--out', (Join-Path $out 'speech-with-emotion.wav'))
            if (Yes "Give it the prosody of a model utterance?`n`nYes: you pick the utterance.`nNo: you name an emotion." $title) { $arguments += @('--model', (Pick 'The model utterance' $wav)) }
            else {
                $emotion = Ask 'Which emotion? SADNESS, HAPPINESS, ANGER, FEAR ...' $title 'SADNESS'
                $degree = Ask 'How strong, from 0 to 1?' $title '0.5'
                $arguments += @('--emotion', $emotion, '--degree', $degree)
            }
        }
        'ARP' {
            $tape = Pick 'The digitised audio of the tape (16-bit WAV)' $wav
            $arguments = @('--tape', $tape, '--out', $out)
            if (Yes "Is there a film of the playback head?`n`nYes: you pick its audio and the folder of its frames (grey PNG files)." $title) {
                $arguments += @('--film-audio', (Pick 'The audio of the film' $wav))
                $f = New-Object Windows.Forms.FolderBrowserDialog; $f.Description = 'The folder of the frames of the film (grey PNG files, in the order of their names)'
                if ($f.ShowDialog() -ne 'OK') { throw 'Cancelled.' }
                $arguments += @('--film-frames', $f.SelectedPath, '--fps', (Ask 'Frames a second of the film' $title '10'))
            }
        }
        'EAE' {
            if (Yes "Make a demonstration capture first?`n`nYes: two real voices in a noisy simulated room (LibriSpeech clips on D:\AI).`nNo: you pick your own capture and array." $title) {
                $clips = 'D:\AI\Datasets\libri_wav\1272\clip.wav', 'D:\AI\Datasets\libri_wav\1462\clip.wav'
                & $exe --demo $out --speech $clips[0] $clips[1]
                $capture = Join-Path $out 'room.wav'; $array = Join-Path $out 'array.json'
            } else {
                $capture = Pick 'The audio of the microphone array: a WAV with a channel for each microphone' $wav
                $array = Pick 'The Microphone Array Geometry (CAE-MAG-V2.5, a JSON file)' 'JSON files (*.json)|*.json'
            }
            $arguments = @('--capture', $capture, '--array', $array, '--out', (Join-Path $out 'result'))
        }
    }
} catch { Write-Host $_.Exception.Message; Start-Sleep 2; exit 1 }

Write-Host "Building $title (a first build takes a while)..." -ForegroundColor Cyan
& dotnet build $project -nologo -v q 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "The build of $project failed." -ForegroundColor Red; exit 1 }
& $exe @arguments
if ($LASTEXITCODE -eq 0) { Write-Host "`nResults in $out" -ForegroundColor Green; Invoke-Item $out } else { Write-Host "`nThe App stopped: see what it said above." -ForegroundColor Yellow }