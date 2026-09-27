<#
.SYNOPSIS
  ON-DEMAND "repo of death" simulation: fabricate a work-scale firmware tree (~50k files, ~90 GB, with
  10+ headers OVER 1 GB - the real repo's headline pathology), optionally stage it on a remote SMB share,
  then index + search it end to end while measuring time, throughput, and peak memory.

.DESCRIPTION
  This is deliberately NOT part of check.ps1 - a full run generates tens of GB and indexes for many minutes,
  so you invoke it by hand when you want to stress the real-scale, real-shape, over-the-wire path.

  It reuses make-firmware-corpus.ps1 (the calibrated generator) for the tree, then:
    1. plants a unique marker near the END of the largest >1 GB header (to test positional/block-selective
       search on a multi-GB file - the thing _bigfile at 308 MB never reached);
    2. with -Network, robocopies the tree to $Dest (a UNC share) and indexes THAT (reads over SMB);
    3. times the index, captures peak working set, and reports throughput;
    4. runs representative queries: a symbol def, a common token, and the near-EOF marker (with the search
       trace, so you see how few bytes a hit deep in a >1 GB file actually pulls over the network).

  Local disk needed ~= TargetGB; with -Network you also need TargetGB free on the share.

.PARAMETER Dest        Where the repo under test lives. Without -Network: generated here. With -Network:
                       the SMB share dir to copy into (e.g. \\IRISH\TestHole\death) - generation is local.
.PARAMETER Network     Generate locally, copy to $Dest (a share), and index over SMB.
.PARAMETER TargetGB    Approx total size (default 90). Big-header count is scaled to approach this.
.PARAMETER GiantHeaders    Count of OVER-1GB headers (default 12 -> the real repo's "10+ >1 GB").
.PARAMETER GiantHeaderGB   Size of each giant header in GB (default 1.2). Must stay < 2 (streaming ceiling).
.PARAMETER Scale       Count multiplier for the rest of the tree (default 1.0 ~= 50k files).
.PARAMETER GenerateOnly  Fabricate (+ stage) only; skip the index/search test.
.PARAMETER Cleanup     Delete the generated tree / staged copy / index cache when done. OFF by default -
                       the death corpus is expensive to build and meant to be REUSED, so it is kept unless
                       you explicitly ask to remove it.

.EXAMPLE
  # Full real-scale run against the mini-PC share (hours, ~90 GB both ends):
  pwsh ./deathrepo-test.ps1 -Network -Dest \\IRISH\TestHole\death

.EXAMPLE
  # Quick local proof that a >1 GB header indexes at all (a few GB, minutes):
  pwsh ./deathrepo-test.ps1 -Dest .corpus/_death_probe -TargetGB 3 -GiantHeaders 1 -Scale 0.05 -Keep
#>
param(
    [Parameter(Mandatory = $true)][string]$Dest,
    [switch]$Network,
    [double]$TargetGB = 90,
    [int]$GiantHeaders = 12,
    [double]$GiantHeaderGB = 1.2,
    [double]$Scale = 1.0,
    [switch]$GenerateOnly,
    [switch]$Cleanup
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$cli = Join-Path $root "src/CodeCompass.Cli/bin/Release/net10.0/CodeCompass.Cli.exe"
if (-not (Test-Path $cli)) { throw "build the Release CLI first: dotnet build -c Release" }
if ($GiantHeaderGB -ge 2) { throw "GiantHeaderGB must be < 2 (per-file streaming ceiling); got $GiantHeaderGB" }

function Say($m) { Write-Host $m -ForegroundColor Cyan }

# Where we generate: locally always (per-file writes over SMB are far too slow); with -Network we then copy.
$genDir = if ($Network) { Join-Path $env:TEMP ("cc-death-" + [Guid]::NewGuid().ToString("N")) } else { $Dest }

# Scale the big-header population to approach TargetGB: giants carry GiantHeaders*GiantHeaderGB; big headers
# (avg ~0.055 GB) make up the rest. This is approximate - the generator reports the true size when done.
$giantGB = $GiantHeaders * $GiantHeaderGB
$remainGB = [Math]::Max(0, $TargetGB - $giantGB)
$bigHeaders = [int][Math]::Max(1, [Math]::Round($remainGB / 0.055))
$giantMB = [int]([Math]::Round($GiantHeaderGB * 1024))

Say "== fabricating work-shape tree (~$TargetGB GB target): $GiantHeaders giants @ ${giantMB}MB (>1GB), ~$bigHeaders big headers, scale $Scale =="
& (Join-Path $root "make-firmware-corpus.ps1") -Out $genDir -Scale $Scale `
    -GiantHeaders $GiantHeaders -MaxHeaderMB $giantMB -BigHeaders $bigHeaders
# Resolve to an ABSOLUTE path: index runs via Start-Process, which does NOT inherit this shell's working
# directory, so a relative -Dest would fail to resolve there.
$genDir = (Resolve-Path $genDir).Path

# Plant a unique marker at the very end of the largest giant header, to exercise positional search on a
# multi-GB file. Appending opens at EOF - it does not rewrite the file.
$giant = Get-ChildItem $genDir -Recurse -Filter "regmap_block*.h" | Sort-Object Length -Descending | Select-Object -First 1
$marker = "ZZ_DEATH_MARKER_" + ([Guid]::NewGuid().ToString("N").Substring(0, 8))
if ($giant) {
    Add-Content -Path $giant.FullName -Value "#define $marker 0x00000001" -Encoding ascii
    Say ("planted marker {0} at end of {1} ({2:N2} GB giant)" -f $marker, $giant.Name, ($giant.Length / 1GB))
}

# Stage to the share if requested.
$repo = $genDir
if ($Network) {
    Say "== staging to $Dest over SMB (robocopy) =="
    $t = [System.Diagnostics.Stopwatch]::StartNew()
    robocopy $genDir $Dest /E /MT:16 /R:1 /W:1 /NFL /NDL /NP /NJH | Select-Object -Last 4
    $t.Stop()
    Say ("copied in {0:N0}s" -f $t.Elapsed.TotalSeconds)
    $repo = $Dest
}

$sizeGB = [math]::Round((Get-ChildItem $genDir -Recurse -File | Measure-Object Length -Sum).Sum / 1GB, 2)
$fileCount = (Get-ChildItem $genDir -Recurse -File).Count
Say "== repo under test: $repo  ($fileCount files, $sizeGB GB) =="

if ($GenerateOnly) { Say "generate-only: skipping index/search. Tree at $genDir$(if($Network){" (staged: $Dest)"})"; exit 0 }

$ErrorActionPreference = 'Continue'  # the CLI writes progress to stderr; don't let it fault a good run

# --- INDEX (timed + peak working set) ---
Say "== INDEX $repo $(if($Network){'(over SMB)'}else{'(local)'}) - timing + peak RSS =="
$idxOut = [System.IO.Path]::GetTempFileName()
$p = Start-Process $cli -ArgumentList @("index", $repo) -WorkingDirectory $root -NoNewWindow -PassThru -RedirectStandardOutput $idxOut -RedirectStandardError ([System.IO.Path]::GetTempFileName())
$peak = 0; $sw = [System.Diagnostics.Stopwatch]::StartNew()
while (-not $p.WaitForExit(1000)) { try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { break } }
$sw.Stop()
Get-Content $idxOut | Select-String 'Indexed |Throughput|Trigram|Symbols' | ForEach-Object { "  " + $_.Line }
"  wall: {0:N0}s   peak RSS: {1:N0} MB" -f $sw.Elapsed.TotalSeconds, ($peak / 1MB)
Remove-Item $idxOut -Force -ErrorAction SilentlyContinue

# --- QUERIES ---
Start-Sleep -Seconds 2   # let the just-exited indexer's cache fully settle before the first query opens it
Say "== queries =="
Write-Host "-- def func_1 --";       & $cli def $repo "func_1" 2>$null | Select-Object -First 2
Write-Host "-- symbols mod0 (5) --"; & $cli symbols $repo "mod0" 2>$null | Select-Object -First 5
if ($giant) {
    Write-Host "-- TRACED search of near-EOF marker in the >1GB giant (bytes pulled vs file size) --"
    $env:CODECOMPASS_SEARCH_TRACE = "1"
    & $cli search $repo $marker 2>&1 | Select-String "$($giant.Name)|search trace|MB read|match" | ForEach-Object { "  " + $_.Line }
    Remove-Item Env:\CODECOMPASS_SEARCH_TRACE -ErrorAction SilentlyContinue
}

# --- CLEANUP (opt-in only: the corpus is expensive and meant to be reused) ---
if ($Cleanup) {
    Say "== cleanup =="
    & $cli cache clear $repo 2>$null | Out-Null
    Remove-Item $genDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($Network) {
        $empty = Join-Path $env:TEMP ("cc-empty-" + [Guid]::NewGuid().ToString("N")); New-Item -ItemType Directory -Force $empty | Out-Null
        robocopy $empty $Dest /MIR /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
        Remove-Item $empty, $Dest -Recurse -Force -ErrorAction SilentlyContinue
    }
    # the generator also drops a sibling *-manifest.json
    Remove-Item ((Split-Path $genDir) + [IO.Path]::DirectorySeparatorChar + (Split-Path $genDir -Leaf) + "-manifest.json") -Force -ErrorAction SilentlyContinue
    Say "cleaned (you asked with -Cleanup)."
} else {
    Say "KEPT (default): $genDir$(if($Network){" (staged: $Dest)"})  - index cache retained. Pass -Cleanup to remove."
}