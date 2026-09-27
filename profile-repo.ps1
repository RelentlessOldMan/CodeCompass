<#
.SYNOPSIS
  Profile a repository's SHAPE - the landscape CodeCompass needs to reason about indexing/query cost and to
  drive per-repo adaptive thresholds. Works on ANY repo; output is ANONYMIZED by default (counts / bytes /
  extensions only - no file names or content), so it's safe to paste from a private work tree.

.DESCRIPTION
  Answers "is this repo huge because of MANY files, or a FEW big files, or a mid-size tail?" and turns that
  into the numbers that decide thresholds (esp. the 8 MB sidecar cutoff). It only STATS files (size/mtime) -
  it never reads content - so it's cheap even over a slow share. Prints a human table plus a compact JSON
  block at the end (paste either).

  This doubles as the spec/prototype for the indexer's built-in landscape pass: the same metrics computed
  here (size histogram, byte concentration, mid-size-tail share, archetype) are what an adaptive index would
  measure during the walk it already does.

.PARAMETER Path        Repo root to profile. Required.
.PARAMETER Json        Emit ONLY the JSON summary (for machine consumption / piping back).
.PARAMETER ShowPaths   Include example file paths in the output (OFF by default; anonymized without it).
.PARAMETER Top         How many extensions to list (default 15).

.EXAMPLE
  pwsh ./profile-repo.ps1 -Path D:\work\firmware        # anonymized landscape report + JSON
.EXAMPLE
  pwsh ./profile-repo.ps1 -Path . -Json > shape.json    # just the machine-readable summary
#>
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [switch]$Json,
    [switch]$ShowPaths,
    [int]$Top = 15
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path)) { throw "not found: $Path" }
$root = (Resolve-Path -LiteralPath $Path).Path

# ---- one stat-only pass (no content reads) ----
$sizes = New-Object System.Collections.Generic.List[long]
$byExt = @{}          # ext -> [count, bytes]
$dirs  = New-Object System.Collections.Generic.HashSet[string]
$maxDepth = 0
$rootDepth = ($root.TrimEnd('\','/') -split '[\\/]').Count

Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
    $sizes.Add($_.Length)
    $e = if ($_.Extension) { $_.Extension.ToLowerInvariant() } else { '(none)' }
    if (-not $byExt.ContainsKey($e)) { $byExt[$e] = @(0, [long]0) }
    $byExt[$e][0]++; $byExt[$e][1] += $_.Length
    [void]$dirs.Add($_.DirectoryName)
    $d = ($_.FullName -split '[\\/]').Count - $rootDepth
    if ($d -gt $maxDepth) { $maxDepth = $d }
}

$n = $sizes.Count
if ($n -eq 0) { throw "no files under $root" }
$total = ($sizes | Measure-Object -Sum).Sum

# ---- size histogram ----
$edges  = @(256KB,1MB,2MB,4MB,8MB,16MB,128MB,1GB)
$labels = @('< 256 KB','256 KB - 1 MB','1 - 2 MB','2 - 4 MB','4 - 8 MB','8 - 16 MB','16 - 128 MB','128 MB - 1 GB','> 1 GB')
$hCount = New-Object 'long[]' 9; $hBytes = New-Object 'double[]' 9
foreach ($s in $sizes) { $i = 0; foreach ($e in $edges) { if ($s -ge $e) { $i++ } else { break } }; $hCount[$i]++; $hBytes[$i] += $s }

# ---- concentration: byte share of the largest X% of files ----
$sorted = $sizes.ToArray(); [Array]::Sort($sorted); [Array]::Reverse($sorted)  # descending
function ByteShareTop([double]$pct) {
    $k = [Math]::Max(1, [int][Math]::Ceiling($n * $pct))
    $sum = 0.0; for ($i = 0; $i -lt $k; $i++) { $sum += $sorted[$i] }
    return $sum / $total
}
$top1 = ByteShareTop 0.01; $top2 = ByteShareTop 0.02; $top5 = ByteShareTop 0.05
$median = $sorted[[int]($n/2)]
$largest = $sorted[0]

# ---- the sidecar-decision numbers: the mid-size tail (currently NO sidecar) vs already sidecar'd ----
# CodeCompass builds a positional sidecar only for files >= 8 MB. Files in 1-8 MB are read WHOLE on a broad
# query. These two figures decide whether a lower (adaptive) sidecar threshold is worth it.
$midLo = 1MB; $midHi = 8MB
$midCount = 0; $midBytes = 0.0; $scCount = 0; $scBytes = 0.0
foreach ($s in $sizes) {
    if ($s -ge $midLo -and $s -lt $midHi) { $midCount++; $midBytes += $s }
    elseif ($s -ge $midHi) { $scCount++; $scBytes += $s }
}

# ---- archetype (a deterministic classification, same logic an adaptive index would use) ----
$archetype = @()
if ($top2 -ge 0.60) { $archetype += 'byte-heavy (a few files hold most bytes)' }
if ($n -ge 20000 -and $median -lt 4KB) { $archetype += 'count-heavy (many tiny files)' }
if ($midBytes / $total -ge 0.10 -and $midCount -ge 200) { $archetype += 'mid-size tail (sidecar-threshold candidate)' }
if ($hCount[8] -ge 1) { $archetype += "$($hCount[8]) file(s) over 1 GB" }
if ($archetype.Count -eq 0) { $archetype += 'ordinary (no strong skew)' }

# ---- output ----
if (-not $Json) {
    "CodeCompass repo profile  (anonymized: counts / bytes / extensions only)"
    "root:   $root"
    "total:  {0:N0} files, {1:N2} GB   dirs: {2:N0}   max depth: {3}" -f $n, ($total/1GB), $dirs.Count, $maxDepth
    ""
    "== size histogram =="
    "{0,-16} {1,9} {2,11} {3,7}" -f 'bucket','files','bytes','%bytes'
    for ($i = 0; $i -lt 9; $i++) {
        "{0,-16} {1,9:N0} {2,9:N2} GB {3,6:N1}%" -f $labels[$i], $hCount[$i], ($hBytes[$i]/1GB), (100.0*$hBytes[$i]/$total)
    }
    ""
    "== byte concentration =="
    "largest single file: {0:N1} MB     median file: {1:N1} KB" -f ($largest/1MB), ($median/1KB)
    "bytes in largest  1% of files: {0,5:N1}%" -f (100*$top1)
    "bytes in largest  2% of files: {0,5:N1}%" -f (100*$top2)
    "bytes in largest  5% of files: {0,5:N1}%" -f (100*$top5)
    ""
    "== sidecar decision (current cutoff 8 MB) =="
    "mid-size tail  1-8 MB (NO sidecar today, read whole on broad queries): {0:N0} files, {1:N2} GB ({2:N1}% of bytes)" -f $midCount, ($midBytes/1GB), (100*$midBytes/$total)
    "already sidecar'd  >= 8 MB:                                            {0:N0} files, {1:N2} GB" -f $scCount, ($scBytes/1GB)
    ""
    "== top $Top extensions by bytes =="
    "{0,-12} {1,9} {2,11} {3,7}" -f 'ext','files','bytes','%bytes'
    $byExt.GetEnumerator() | Sort-Object { $_.Value[1] } -Descending | Select-Object -First $Top | ForEach-Object {
        "{0,-12} {1,9:N0} {2,9:N2} GB {3,6:N1}%" -f $_.Key, $_.Value[0], ($_.Value[1]/1GB), (100.0*$_.Value[1]/$total)
    }
    ""
    "== archetype =="
    $archetype | ForEach-Object { "  - $_" }
    ""
    "(paste the JSON below too if convenient - it's the machine-readable version)"
    ""
}

# ---- JSON summary (always; -Json = only this) ----
$hist = @()
for ($i = 0; $i -lt 9; $i++) { $hist += [ordered]@{ bucket = $labels[$i]; files = $hCount[$i]; gb = [math]::Round($hBytes[$i]/1GB,3); pctBytes = [math]::Round(100*$hBytes[$i]/$total,2) } }
$exts = $byExt.GetEnumerator() | Sort-Object { $_.Value[1] } -Descending | Select-Object -First $Top | ForEach-Object {
    [ordered]@{ ext = $_.Key; files = $_.Value[0]; gb = [math]::Round($_.Value[1]/1GB,3) }
}
$summary = [ordered]@{
    schema      = 'codecompass-repo-profile/1'
    totalFiles  = $n
    totalGB     = [math]::Round($total/1GB,3)
    dirs        = $dirs.Count
    maxDepth    = $maxDepth
    largestFileMB = [math]::Round($largest/1MB,2)
    medianFileKB  = [math]::Round($median/1KB,2)
    byteShareTop1pct = [math]::Round($top1,4)
    byteShareTop2pct = [math]::Round($top2,4)
    byteShareTop5pct = [math]::Round($top5,4)
    midTail_1to8MB   = [ordered]@{ files = $midCount; gb = [math]::Round($midBytes/1GB,3); pctBytes = [math]::Round(100*$midBytes/$total,2) }
    sidecarable_ge8MB = [ordered]@{ files = $scCount; gb = [math]::Round($scBytes/1GB,3) }
    histogram   = $hist
    topExtensions = $exts
    archetype   = $archetype
}
"===JSON==="
$summary | ConvertTo-Json -Depth 6
