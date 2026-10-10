<#
.SYNOPSIS
  Copies the part of the DI tree that is needed to build Thalia (the MAS-App) into a new folder,
  keeping the folder and file structure.

.DESCRIPTION
  -Source   the full tree (for example D:\DI)
  -Dest     where the Thalia tree goes (for example D:\CI-new); must not exist, or be empty
  -CheckOnly  report only; copy nothing

  It never deletes anything and never writes to -Source. It takes the project folders from the
  .csproj files themselves (ProjectReference, from the entry projects in entries.txt), so a project
  added to the Source since this manifest was written is found; it reports how that list differs
  from projects.txt. Then it copies data.txt (descriptors, apps, workflows, assets, schemas, notices).
  Not copied: bin, obj, .vs, *.user, *.pdb, SharedStorage, Models, TestData.
  Not tested on Windows by its author: run with -CheckOnly first.
#>
param(
  [Parameter(Mandatory)][string]$Source,
  [Parameter(Mandatory)][string]$Dest,
  [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$dst  = [IO.Path]::GetFullPath($Dest).TrimEnd('\')
if ($dst.StartsWith($src + '\') -or $src.StartsWith($dst + '\') -or $src -eq $dst) { throw 'Source and Dest must not contain one another.' }
if (-not $CheckOnly -and (Test-Path $dst) -and (Get-ChildItem $dst -Force | Select-Object -First 1)) { throw "Dest '$dst' is not empty: nothing was written. Choose a new folder." }

function Read-List($f) {
  Get-Content $f | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $_ -replace '/', '\' }
}
function Get-Refs($proj) {
  $x = New-Object System.Xml.XmlDocument; $x.Load($proj)
  foreach ($r in $x.SelectNodes('//ProjectReference')) {
    [IO.Path]::GetFullPath((Join-Path (Split-Path $proj) ($r.Include -replace '/', '\')))
  }
}

# 1. the closure of the entry projects, from the Source's own .csproj files
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$todo = [System.Collections.Generic.Stack[string]]::new()
foreach ($e in Read-List (Join-Path $here 'entries.txt')) { $todo.Push((Join-Path $src $e)) }
while ($todo.Count) {
  $p = $todo.Pop()
  if (-not $seen.Add($p)) { continue }
  if (-not (Test-Path $p)) { Write-Warning "Missing project: $p"; continue }
  foreach ($r in Get-Refs $p) { $todo.Push($r) }
}
$found = $seen | ForEach-Object { (Split-Path $_).Substring($src.Length + 1) } | Sort-Object -Unique
$listed = Read-List (Join-Path $here 'projects.txt')

# 2. what differs from the manifest
$new  = $found  | Where-Object { $listed -notcontains $_ -and $_ -ne 'MAS\Service\src' }
$gone = $listed | Where-Object { $found  -notcontains $_ -and $_ -ne 'MAS\Service' }
Write-Host "Projects found from the entry projects: $($found.Count)"
if ($new)  { Write-Host 'In the Source but NOT in projects.txt (they will be copied):'; $new  | ForEach-Object { "  + $_" } }
if ($gone) { Write-Host 'In projects.txt but not reached from the Source (they will NOT be copied):'; $gone | ForEach-Object { "  - $_" } }

$folders = @($found) + @('MAS\Service') | Sort-Object -Unique   # the Service folder also holds its scripts and settings
$data = Read-List (Join-Path $here 'data.txt')
$missing = @($data + $folders) | Where-Object { -not (Test-Path (Join-Path $src $_)) }
if ($missing) { Write-Host 'Not found in the Source:'; $missing | ForEach-Object { "  ? $_" } }
if ($CheckOnly) { Write-Host 'Check only: nothing copied.'; return }

# 3. copy
$xd = 'bin','obj','.vs','SharedStorage','Models','TestData'
$xf = '*.user','*.pdb'
foreach ($rel in $folders + ($data | Where-Object { (Test-Path (Join-Path $src $_) -PathType Container) })) {
  $null = robocopy (Join-Path $src $rel) (Join-Path $dst $rel) /E /XD $xd /XF $xf /NFL /NDL /NJH /NJS /NP
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed for $rel ($LASTEXITCODE)" }
}
foreach ($rel in $data | Where-Object { Test-Path (Join-Path $src $_) -PathType Leaf }) {
  $to = Join-Path $dst $rel
  New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
  Copy-Item (Join-Path $src $rel) $to
}

# 4. nothing in the copy may name the folders it came from
$bad = Get-ChildItem $dst -Recurse -File -Include *.cs,*.json,*.orch,*.csproj,*.ps1,*.bat,*.md,*.razor,*.html,*.js |
  Select-String -Pattern 'D:\\(DI|CI)\b','D:/(DI|CI)\b' -List
if ($bad) { Write-Warning 'These files name D:\DI or D:\CI; fix them:'; $bad | ForEach-Object { "  $($_.Path)" } } else { Write-Host 'No file names D:\DI or D:\CI.' }
Write-Host "Done: $dst"
