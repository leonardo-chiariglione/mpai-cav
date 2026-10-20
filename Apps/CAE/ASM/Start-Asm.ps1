# AUDIO SCENE MANAGEMENT, STARTED AS ITS PARTS ARE: the Module's AIMs on an AIM host (a process of its own), the Controller (AsmServer, another), and the
# ASM User Agent (ASM Studio, a third) that reaches the Controller through the Controller API only. The three talk through APIs; when the User Agent is closed
# the other two are ended. What each says is kept in AllApps\logs (asm-aimhost.log, asm-controller.log).
#
#   Start-Asm.ps1 [-Root <the software tree>] [-Logs <folder>]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [string]$Logs = (Join-Path $Root 'AllApps\logs'),
    [int]$ControllerPort = 5109,
    [switch]$NoStudio     # start the AIM host and the Controller, check that they run, and end them: a check without the window
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Logs | Out-Null
Set-Location $Root

function Build($project) {
    & dotnet build $project -nologo -v q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The build of $project failed." }
}
Write-Host 'Building (a first build takes a while)...'
Build 'Apps\CAE\ASM\Host\AsmHost.csproj'
Build 'AIF\AimHost\AIF.AimHost.csproj'
Build 'Apps\CAE\ASM\Server\AsmServer.csproj'
Build 'AIF\UserAgent\Clients\AsmStudio\AsmStudio.csproj'

if (Get-NetTCPConnection -State Listen -LocalPort $ControllerPort -ErrorAction SilentlyContinue) {
    throw "Port $ControllerPort is in use: another Audio Scene Management Controller is running. Close it first."
}

# A key for this run only: the AIM host admits the Controller that holds it.
$key = [Guid]::NewGuid().ToString('N')
$libs = Join-Path $Root 'Apps\CAE\ASM\Host\bin\Debug\net10.0'
$hostLog = Join-Path $Logs 'asm-aimhost.log'
$ctlLog  = Join-Path $Logs 'asm-controller.log'
$storage = Join-Path $env:USERPROFILE 'Documents\ASM Studio\module-storage'
$hostProcess = $null; $controller = $null

try {
    # 1. THE MODULE'S AIMs, on an AIM host.
    $hostArgs = @((Join-Path $Root 'AIF\AimHost\bin\Debug\net10.0\AIF.AimHost.dll'), '--port', '0', '--key', $key,
        '--amds', (Join-Path $Root 'AIMs\AMDs'), '--settings', (Join-Path $Root 'AIMs\aim-settings.json'),
        '--storage', (Join-Path $env:TEMP "mpai-asm-aimhost-$PID"),
        '--provider', ((Join-Path $libs 'Mpai.Cae.Asm.dll') + ':Mpai.Cae.Asm.AsmProvider'),
        '--provider', ((Join-Path $libs 'Mpai.Providers.dll') + ':Mpai.Providers.MatProvider'))
    Remove-Item $hostLog -ErrorAction SilentlyContinue
    $hostProcess = Start-Process dotnet -ArgumentList ($hostArgs | ForEach-Object { '"' + $_ + '"' }) -WorkingDirectory $Root -WindowStyle Hidden -PassThru -RedirectStandardOutput $hostLog -RedirectStandardError ($hostLog + '.err')
    $port = $null
    foreach ($i in 1..120) {
        Start-Sleep -Milliseconds 500
        if ($hostProcess.HasExited) { throw "The AIM host stopped: see $hostLog" }
        if ((Test-Path $hostLog) -and ((Get-Content $hostLog -Raw -ErrorAction SilentlyContinue) -match 'listening on (\d+)')) { $port = $Matches[1]; break }
    }
    if (-not $port) { throw "The AIM host did not start: see $hostLog" }
    Write-Host "The AIMs of the Module run on the AIM host at port $port."

    # 2. THE CONTROLLER, which finds them there.
    $hosts = [ordered]@{}
    foreach ($aim in '1CAE-AOE-V1.0-I01', '1CAE-ASE-V1.0-I01', '1CAE-SOE-V1.0-I01', '1CAE-SSE-V1.0-I01', '1OSD-BME-V1.5-I01', '1MMC-TST-V2.5-I01') { $hosts[$aim] = "localhost:$port" }
    $config = Join-Path $env:TEMP "mpai-asm-controller-$PID.json"
    [ordered]@{ ListenUrl = "https://localhost:$ControllerPort/"; AmdDirectory = (Join-Path $Root 'AIMs\AMDs'); SettingsPath = (Join-Path $Root 'AIMs\aim-settings.json');
                AimHosts = $hosts; AimHostKey = $key; SharedStorage = $storage } | ConvertTo-Json -Depth 4 | Set-Content $config -Encoding UTF8
    Remove-Item $ctlLog -ErrorAction SilentlyContinue
    $controller = Start-Process (Join-Path $Root 'Apps\CAE\ASM\Server\bin\Debug\net10.0\AsmServer.exe') -ArgumentList ('"' + $config + '"') -WorkingDirectory $Root -WindowStyle Hidden -PassThru -RedirectStandardOutput $ctlLog -RedirectStandardError ($ctlLog + '.err')
    $ready = $false
    foreach ($i in 1..360) {
        Start-Sleep -Milliseconds 500
        if ($controller.HasExited) { throw "The Controller stopped: see $ctlLog" }
        if ((Test-Path $ctlLog) -and ((Get-Content $ctlLog -Raw -ErrorAction SilentlyContinue) -match '\[MAS\] Listening')) { $ready = $true; break }
    }
    if (-not $ready) { throw "The Controller did not start: see $ctlLog" }
    Write-Host "The Controller listens at https://localhost:$ControllerPort/."

    if ($NoStudio) { Write-Host 'Checked: the AIM host and the Controller run.'; return }

    # 3. THE USER AGENT, which knows only the Controller API and where the Controller is.
    $env:MPAI_MAS_SERVER = "https://localhost:$ControllerPort/"
    Write-Host 'ASM Studio is starting. Close its window to end the session.'
    $studio = Start-Process (Join-Path $Root 'AIF\UserAgent\Clients\AsmStudio\bin\Debug\net10.0-windows10.0.19041.0\AsmStudio.exe') -PassThru
    $studio.WaitForExit()
}
finally {
    foreach ($p in @($controller, $hostProcess)) { if ($p -and -not $p.HasExited) { try { $p.Kill($true) } catch { } } }
}
