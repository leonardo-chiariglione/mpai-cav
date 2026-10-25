<#
  Build-Packages.ps1 - build each AIM's package, where its L3 says the package is.

  For every L3 in -Amds that has no Sub-AIMs (a leaf AIM) and names a BinaryName,
  this finds the project that builds that assembly and publishes it into
  -Packages\<ImplementationID>\ - the folder the L3's ImplementationURI names when
  the L3 is submitted to the Store with Submit-L3s.ps1 -Packages <the same folder>.

  A package is an AIM's build output for one machine: its assembly, which carries
  the plug-in (IAimPlugin), and the libraries it needs. The Service, configured
  with "AimSource": "Packages", loads AIMs from these instead of from the
  providers compiled into it.

  -Amds      where the L3s are (e.g. D:\BI\AIMs\AMDs). Required.
  -Packages  where the packages go (e.g. D:\MPAI\Packages). Required.
  -Ids       which AIMs, by Instance id; default: every leaf L3 in -Amds
  -Configuration  Release (default) or Debug
  -Runtime   the platform to build for, for example win-x64 or linux-x64; by default the one the L3 names (its OperatingSystem and
             Architecture). Only that platform's native libraries go into the package.
  -ZipTo     a folder where each package is also zipped, as <ImplementationID>-<runtime>.zip: what is published for the Store.
#>
param(
    [Parameter(Mandatory = $true)][string]$Amds,
    [Parameter(Mandatory = $true)][string]$Packages,
    [string[]]$Ids,
    [string]$Configuration = 'Release',
    [string]$Runtime,
    [string]$ZipTo
)
$ErrorActionPreference = 'Stop'
$Ids = @($Ids | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Ids.Count -eq 0) { $Ids = Get-ChildItem $Amds -Filter *.json | ForEach-Object { $_.BaseName } }

# Every project under this folder, by the assembly it builds.
$root = Split-Path $PSScriptRoot -Parent
$projects = @{}
foreach ($p in Get-ChildItem $root -Recurse -Filter *.csproj -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $name = ([xml](Get-Content $p.FullName -Raw)).Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1
    if (-not $name) { $name = $p.BaseName }
    if (-not $projects.ContainsKey($name)) { $projects[$name] = $p.FullName }
}

$built = 0; $skipped = 0
foreach ($id in $Ids) {
    $file = Join-Path $Amds "$id.json"
    if (-not (Test-Path $file)) { Write-Host "  no L3 for $id" -ForegroundColor Red; continue }
    $l3 = Get-Content $file -Raw | ConvertFrom-Json
    if (@($l3.SubAIMs).Where({ $_ }).Count -gt 0) { continue }        # a composite needs no package of its own
    $impl = @($l3.Implementations)[0]
    if (-not $impl -or -not $impl.BinaryName) { Write-Host "  $id names no BinaryName" -ForegroundColor Yellow; $skipped++; continue }
    if (-not $projects.ContainsKey($impl.BinaryName)) {
        Write-Host "  $id : no project builds $($impl.BinaryName)" -ForegroundColor Yellow; $skipped++; continue
    }
    $into = Join-Path $Packages $l3.Identifier.ImplementationID
    # The platform: what the L3 names, unless told. A package carries the native libraries of that platform and no other.
    $rid = $Runtime
    if (-not $rid) {
        $os   = switch -Wildcard ("$($impl.OperatingSystem)") { 'Windows' { 'win' } 'Linux' { 'linux' } 'MacOS' { 'osx' } default { '' } }
        $arch = switch -Wildcard ("$($impl.Architecture)")    { 'x86-64' { 'x64' } 'ARM64' { 'arm64' } default { '' } }
        $rid = if ($os -and $arch) { "$os-$arch" } else { '' }
    }
    Write-Host "  $id -> $into ($(if ($rid) { $rid } else { 'any platform' }))"
    if (Test-Path $into) { Remove-Item $into -Recurse -Force }
    $ridArgs = if ($rid) { @('-r', $rid, '--self-contained', 'false') } else { @() }
    dotnet publish $projects[$impl.BinaryName] -c $Configuration -o $into @ridArgs -p:DebugType=none -p:DebugSymbols=false --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "    FAILED to build $($impl.BinaryName)" -ForegroundColor Red; $skipped++; continue }
    if (-not (Test-Path (Join-Path $into "$($impl.BinaryName).dll"))) {
        Write-Host "    built, but $($impl.BinaryName).dll is not in the package" -ForegroundColor Red; $skipped++; continue
    }
    if ($ZipTo) {
        New-Item -ItemType Directory -Force $ZipTo | Out-Null
        $zip = Join-Path $ZipTo "$($l3.Identifier.ImplementationID)-$(if ($rid) { $rid } else { 'any' }).zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $into '*') -DestinationPath $zip -CompressionLevel Optimal
        Write-Host ("    {0}  {1:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB))
    }
    $built++
}
Write-Host "$built package(s) built, $skipped not. Submit the L3s again so the Store sees them:" -ForegroundColor Green
Write-Host "  Submit-L3s.ps1 -Folder $Amds -Packages $Packages"
