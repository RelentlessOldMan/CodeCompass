<#
.SYNOPSIS
  Fabricate a large (>10 GB) corpus for scale/memory testing by aggregating the already-
  fetched benchmark repos into one tree (real copies, since the walker skips junctions).

.DESCRIPTION
  No single public repo is dozens of GB, so this stacks copies of the fetched corpora until
  it reaches the target size. Content is duplicated, so distinct-trigram memory is a bit
  understated versus a real repo of that size - but file/symbol counts, build time, and the
  memory-mapped index behavior are exercised realistically. Uses robocopy for speed and to
  tolerate deep paths / odd permissions.

  Run fetch-corpus.ps1 first (ideally the medium/large tiers). Then benchmark with:
      dotnet run -c Release --project src/CodeCompass.Bench -- run <megacorpus path>

.PARAMETER TargetGB   Target size (default 12).
.PARAMETER Out        Output dir (default .corpus/_mega).
#>
param(
    [double]$TargetGB = 12,
    [string]$Out
)

$ErrorActionPreference = "Stop"
$corpus = if ($env:CODECOMPASS_CORPUS_DIR) { $env:CODECOMPASS_CORPUS_DIR } else { Join-Path $PSScriptRoot ".corpus" }
if (-not $Out) { $Out = Join-Path $corpus "_mega" }

if (-not (Get-Command robocopy -ErrorAction SilentlyContinue)) { throw "robocopy not found (ships with Windows)" }

$sources = Get-ChildItem $corpus -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "_mega" } |
    ForEach-Object { $sub = Get-ChildItem $_.FullName -Directory | Select-Object -First 1; if ($sub) { $sub.FullName } else { $_.FullName } }

if (-not $sources) { throw "no fetched corpora in $corpus - run fetch-corpus.ps1 first" }

# Purge any previous (possibly partial) megacorpus via a mirror-from-empty (robocopy can
# clear trees Remove-Item chokes on).
$empty = Join-Path $env:TEMP ("cc-empty-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $empty | Out-Null
if (Test-Path $Out) { robocopy $empty $Out /MIR /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null }
Remove-Item $empty -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$srcSizes = @{}
foreach ($s in $sources) {
    $srcSizes[$s] = (Get-ChildItem $s -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
}

$targetBytes = [long]($TargetGB * 1GB)
$copied = 0L
$round = 0
while ($copied -lt $targetBytes -and $round -lt 200) {
    foreach ($src in $sources) {
        if ($copied -ge $targetBytes) { break }
        $dest = Join-Path $Out ("c{0:D2}_{1}" -f $round, (Split-Path $src -Leaf))
        Write-Host ("copying {0} -> {1}" -f (Split-Path $src -Leaf), (Split-Path $dest -Leaf))
        robocopy $src $dest /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { Write-Warning "robocopy reported issues for $src (code $LASTEXITCODE); continuing" }
        $copied += [long]$srcSizes[$src]
    }
    $round++
}

$gb = [math]::Round($copied / 1GB, 2)
Write-Host ""
Write-Host "megacorpus ready: $Out  (~$gb GB, $round round(s))"
Write-Host "benchmark it:  dotnet run -c Release --project src/CodeCompass.Bench -- run `"$Out`""
exit 0  # robocopy leaves a non-zero bitmask in $LASTEXITCODE even on success; don't propagate it
