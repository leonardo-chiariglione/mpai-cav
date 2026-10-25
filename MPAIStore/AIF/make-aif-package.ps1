<#
.SYNOPSIS
  Makes the AIF of a user of the MPAI Store for Windows (x86-64): the Controller, in the MPAI-MAS Service that runs Modules, and the AIM host.

.DESCRIPTION
  The AIF is what a user of the Store needs to run what the Store lists: it takes the L3s from where they are (a folder, or the Store), obtains the package of
  each AIM from the address its L3 names, obtains the data each AIM declares (AccessSources), verifies what it runs and runs the Module.

  It publishes the Service (Apps\MAS\Service) and the AIM host (AIF\AimHost) for win-x64 only, framework-dependent (it needs the .NET 10 runtime), with no
  debugging symbols, and zips them with the README and an example of the configuration as MPAIStore\Packages\AIF-V3.0-win-x64.zip. It writes
  MPAIStore\AIF\aif-V3.0-win-x64.json: what the Store lists of this build - the version, the environment, the address (to be filled in when the
  zip is published) and the SHA-256 and the size of the zip.

.EXAMPLE
  powershell -File MPAIStore\AIF\make-aif-package.ps1 -Address https://github.com/leonardo-chiariglione/packages/releases/download/aif-v3.0/AIF-V3.0-win-x64.zip
#>
param([string]$Address = '')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$work = Join-Path $env:TEMP 'mpai-aif-win-x64'
$zip  = Join-Path $root 'MPAIStore\Packages\AIF-V3.0-win-x64.zip'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null

foreach ($p in @(@('service', 'Apps\MAS\Service\src\MasService.csproj'), @('aimhost', 'AIF\AimHost\AIF.AimHost.csproj'))) {
    Write-Host "== $($p[1])"
    & dotnet publish (Join-Path $root $p[1]) -c Release -r win-x64 --self-contained false -p:DebugType=none -p:DebugSymbols=false -o (Join-Path $work $p[0]) --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "$($p[1]) did not publish." }
}
Copy-Item (Join-Path $PSScriptRoot 'README-AIF.md') (Join-Path $work 'README.md')
Copy-Item (Join-Path $PSScriptRoot 'mas-server.store.example.json') (Join-Path $work 'mas-server.store.example.json')

New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -CompressionLevel Optimal

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
$size = (Get-Item $zip).Length
$description = [ordered]@{
    Name            = 'AIF'
    Description     = 'The AI Framework of a user of the MPAI Store: the Controller (in the MPAI-MAS Service) and the AIM host. It takes the L3s from a folder or from the Store, obtains the package of each AIM and the data each AIM declares, verifies them, and runs the Module.'
    Version         = '3.0'
    OperatingSystem = 'Windows'
    Architecture    = 'x86-64'
    Requires        = '.NET 10 runtime (x64)'
    Address         = $Address
    Sha256          = $sha
    SizeBytes       = $size
    EntryPoint      = 'service\MasService.exe <configuration>'
}
$description | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'aif-V3.0-win-x64.json') -Encoding UTF8
Write-Host ("{0}  {1:N1} MB  {2}" -f (Split-Path $zip -Leaf), ($size / 1MB), $sha)
