<#
.SYNOPSIS
  Fabricate a synthetic firmware repo that reproduces the measured SHAPE of a large proprietary C firmware
  tree (see codecompass-synthetic-corpus-*.txt), as REUSABLE regression infrastructure: each structural
  property that has ever caused a defect is an INDEPENDENT knob, and the generator emits a ground-truth
  manifest of every symbol's definition + reference sites so find_references/find_callees become pass/fail
  assertions instead of eyeballed counts.

.DESCRIPTION
  The one fact that matters: ~90% of bytes live in ~2% of files (giant generated register headers with ~1M
  #defines each). Scale by COUNTS, never per-file size - the high-macro-density headers ARE the pathology,
  so keep at least one at any scale. Deterministic given -Seed. No compile_commands.json by default (the
  firmware norm and the best-effort-clang trigger); -CompileDb partial|full generates one as an A/B control.

  Knobs map 1:1 to observed failure modes (spec 3): -MacroDensity (preprocessor memory), -BlobFiles (parse
  cost), -MaxHeaderMB (streaming), -CompileDb (disclosure/fallback), -BuildOutput (exclusion + lexical
  noise), -TinyFiles (per-file overhead / SMB stat pressure), -Dirs/-Depth (path handling), -CrossRefs
  (reference correctness), -LinkedRoots (federation). Dial one against a fixed baseline to attribute a
  regression.

.PARAMETER Out           Output directory (created; cleared if it exists).
.PARAMETER Scale         Multiplier on the count knobs (default 0.01). Per-file sizes are NOT scaled.
.PARAMETER Seed          RNG seed for deterministic output (default 1337).
.PARAMETER MacroDensity  #defines per giant register header (default 1,000,000) - the memory-bug axis.
.PARAMETER GiantHeaders  Count of high-macro-density ~MaxHeaderMB headers (full-repo 178; kept >=1 if >0).
.PARAMETER BigHeaders    Count of 10-100 MB headers (full-repo 655).
.PARAMETER MedHeaders    Count of 1-10 MB headers (full-repo 1957).
.PARAMETER OrdinaryHeaders Count of ~6.6 KB headers (full-repo 7400).
.PARAMETER CFiles        Count of .c files (~628 lines, cross-dir call edges) (full-repo 5123).
.PARAMETER TinyFiles     Count of ~1 KB .csv (file-COUNT pressure) (full-repo 20586).
.PARAMETER BlobFiles     Count of high-byte zero-symbol data blobs (parse-cost axis).
.PARAMETER MaxHeaderMB   Size of each giant header (default 110). The pathology; never scaled down.
.PARAMETER CompileDb     none | partial | full - emit a compile_commands.json covering none/half/all .c.
.PARAMETER BuildOutput   Emit .o/.elf/.lst/.a/.bak beside sources (default $true).
.PARAMETER Dirs          Approximate directory count (full-repo 5700).
.PARAMETER Depth         Approximate max path depth (full-repo 18).
.PARAMETER LinkedRoots   Split the .c files across N sibling trees (federation). Default 1.
.PARAMETER Manifest      Emit _corpus-manifest.json of ground-truth def/ref sites (default $true).
.PARAMETER Run           Index it and measure a find_references (time + peak RSS).
.PARAMETER Verify        Run the correctness oracle: for a sample of symbols, assert find_references matches
                         the manifest. Requires the Release CLI.

.EXAMPLE
  # Fast correctness-oracle smoke (no giant headers): seconds.
  pwsh ./make-firmware-corpus.ps1 -Out .corpus/_fw -Scale 0.01 -GiantHeaders 0 -BigHeaders 0 -MedHeaders 0 -Verify
.EXAMPLE
  # Memory-axis repro: one giant header + the .c that includes it.
  pwsh ./make-firmware-corpus.ps1 -Out .corpus/_fw -GiantHeaders 1 -CFiles 5 -OrdinaryHeaders 0 -TinyFiles 0 -Run
.EXAMPLE
  # CI tier (~1/100, keeps one pathology header):
  pwsh ./make-firmware-corpus.ps1 -Out .corpus/_fw -Scale 0.01
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [double]$Scale = 0.01,
    [int]$Seed = 1337,
    [int]$MacroDensity = 1000000,
    [int]$GiantHeaders = 178,
    [int]$BigHeaders = 655,
    [int]$MedHeaders = 1957,
    [int]$OrdinaryHeaders = 7400,
    [int]$CFiles = 5123,
    [int]$TinyFiles = 20586,
    [int]$BlobFiles = 50,
    [int]$MaxHeaderMB = 110,
    [ValidateSet('none', 'partial', 'full')][string]$CompileDb = 'none',
    [bool]$BuildOutput = $true,
    [int]$Dirs = 5700,
    [int]$Depth = 8,
    [int]$LinkedRoots = 1,
    [bool]$Manifest = $true,
    [switch]$Run,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$rng = [System.Random]::new($Seed)

# Scaled count: round(knob * Scale). A "pathology" population (keepOne) never drops below 1 while enabled,
# so even --scale 0.001 keeps a giant header; a knob set to 0 disables that population entirely.
function ScaledCount([int]$knob, [bool]$keepOne = $false) {
    if ($knob -le 0) { return 0 }
    $n = [int][Math]::Round($knob * $Scale)
    if ($keepOne -and $n -lt 1) { $n = 1 }
    return $n
}

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$outFull = (New-Item -ItemType Directory -Force -Path $Out).FullName
Write-Host "Fabricating firmware corpus at $outFull (scale $Scale, seed $Seed, compileDb=$CompileDb) ..."

$nGiant  = ScaledCount $GiantHeaders  $true
$nBig    = ScaledCount $BigHeaders    $false
$nMed    = ScaledCount $MedHeaders    $false
$nSmallH = ScaledCount $OrdinaryHeaders $false
$nC      = [Math]::Max(2, (ScaledCount $CFiles $false))   # need >=2 for a cross-file reference edge
$nCsv    = ScaledCount $TinyFiles     $false
$nBlob   = ScaledCount $BlobFiles     $false

# Directory tree: root plus LinkedRoots-1 sibling trees, each block/sub/mod, up to ~Depth deep.
$rootsList = New-Object System.Collections.Generic.List[string]
$rootsList.Add($outFull)
for ($r = 1; $r -lt [Math]::Max(1, $LinkedRoots); $r++) {
    $rootsList.Add((New-Item -ItemType Directory -Force -Path (Join-Path (Split-Path $outFull) ((Split-Path $outFull -Leaf) + "_root$r"))).FullName)
}
$dirList = New-Object System.Collections.Generic.List[string]
$perRoot = [Math]::Max(1, [int][Math]::Round((ScaledCount $Dirs $false) / $rootsList.Count / 9))
foreach ($rt in $rootsList) {
    for ($b = 0; $b -lt ([Math]::Max(1, $perRoot)); $b++) {
        for ($s = 0; $s -lt 3; $s++) {
            for ($m = 0; $m -lt 3; $m++) {
                $seg = @("block$b", "sub$s", "mod$m")
                # pad toward -Depth so long/deep paths are exercised
                while ($seg.Count -lt [Math]::Min($Depth, 12)) { $seg += "lvl$($seg.Count)" }
                $d = Join-Path $rt ($seg -join [System.IO.Path]::DirectorySeparatorChar)
                New-Item -ItemType Directory -Force -Path $d | Out-Null
                $dirList.Add($d)
            }
        }
    }
}
function PickDir { return $dirList[$rng.Next(0, $dirList.Count)] }

# Register-map header: MacroDensity object-like #defines (3 per register, ~90 chars/line), capped at
# MaxHeaderMB. Density is INDEPENDENT of size: -MacroDensity high + -MaxHeaderMB low still stresses the
# preprocessor macro table (the actual bug axis) without the bytes.
function New-RegHeader([string]$path, [int]$defines, [long]$maxBytes, [int]$fam) {
    $guard = "REGMAP_" + [System.IO.Path]::GetFileNameWithoutExtension($path).ToUpper() + "_H"
    $sw = [System.IO.StreamWriter]::new($path)
    try {
        $sw.WriteLine("#ifndef $guard"); $sw.WriteLine("#define $guard")
        $sw.WriteLine("/* generated hardware register map - block $fam (synthetic) */")
        $reg = 0; $emitted = 0
        while ($emitted -lt $defines -and $sw.BaseStream.Length -lt $maxBytes) {
            $addr = ($reg * 4).ToString("x8")
            $sw.WriteLine("#define HWIO_BLK${fam}_REG${reg}_ADDR (BASE_BLOCK$fam + 0x$addr)")
            $sw.WriteLine("#define HWIO_BLK${fam}_REG${reg}_RMSK 0x000000ff")
            $sw.WriteLine("#define HWIO_BLK${fam}_REG${reg}_IN in_dword(HWIO_BLK${fam}_REG${reg}_ADDR)")
            $reg++; $emitted += 3
        }
        $sw.WriteLine("#endif")
    } finally { $sw.Close() }
}

Write-Host "  register headers: $nGiant giant (density $MacroDensity, <=${MaxHeaderMB}MB) + $nBig big + $nMed medium ..."
$giantPaths = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $nGiant; $i++) {
    $p = Join-Path (PickDir) "regmap_block$i.h"
    New-RegHeader $p $MacroDensity ([long]$MaxHeaderMB * 1MB) $i
    $giantPaths.Add($p)
}
for ($i = 0; $i -lt $nBig; $i++) { New-RegHeader (Join-Path (PickDir) "regbig_$i.h") 2000000 ([long]$rng.Next(10, 100) * 1MB) (100 + $i) }
for ($i = 0; $i -lt $nMed; $i++) { New-RegHeader (Join-Path (PickDir) "regmed_$i.h") 200000  ([long]$rng.Next(1, 10)  * 1MB) (1000 + $i) }

Write-Host "  $nSmallH ordinary headers ..."
for ($i = 0; $i -lt $nSmallH; $i++) {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("#ifndef HDR_${i}_H"); [void]$sb.AppendLine("#define HDR_${i}_H")
    [void]$sb.AppendLine("#include <stddef.h>")
    [void]$sb.AppendLine("typedef struct mod${i}_ctx { int id; unsigned flags; } mod${i}_ctx_t;")
    for ($f = 0; $f -lt 30; $f++) { [void]$sb.AppendLine("int mod${i}_op${f}(mod${i}_ctx_t* c, int arg);") }
    [void]$sb.AppendLine("#endif")
    Set-Content (Join-Path (PickDir) "hdr_$i.h") $sb.ToString() -Encoding utf8
}

# High-byte, zero-symbol data blobs (the tree-sitter parse-cost axis): dense numeric arrays, no symbols.
for ($i = 0; $i -lt $nBlob; $i++) {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("static const unsigned char blob_$i[] = {")
    for ($l = 0; $l -lt 40000; $l++) { [void]$sb.AppendLine("0x$($rng.Next(0,255).ToString('x2')),0x$($rng.Next(0,255).ToString('x2')),0x$($rng.Next(0,255).ToString('x2')),0x$($rng.Next(0,255).ToString('x2')),") }
    [void]$sb.AppendLine("};")
    Set-Content (Join-Path (PickDir) "blob_$i.c") $sb.ToString() -Encoding ascii
}

# .c files with CROSS-FILE CROSS-DIRECTORY call edges: func_i defined in src_i.c CALLS func_{i-1} (in
# another dir). We record the ground-truth def + ref sites as we emit, for the correctness oracle. The
# first few .c include a giant header (the per-TU macro-blob stressor).
Write-Host "  $nC .c files (cross-ref edges) ..."
$groundTruth = @{}   # symbol -> @{ def = "path:line"; refs = @() }
$cInfo = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $nC; $i++) {
    $dir = PickDir
    $p = Join-Path $dir "src_$i.c"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("#include <stddef.h>")
    for ($inc = 0; $inc -lt 6; $inc++) { if ($nSmallH -gt 0) { $lines.Add("#include ""hdr_$($rng.Next(0,$nSmallH)).h""") } }
    if ($i -lt 3 -and $giantPaths.Count -gt 0) { $lines.Add("#include ""$([System.IO.Path]::GetFileName($giantPaths[$i % $giantPaths.Count]))""") }
    $lines.Add("int func_$i(int x);")
    $callLine = 0
    if ($i -gt 0) { $lines.Add("int func_$($i-1)(int x);") }
    $lines.Add("int func_$i(int x) {")
    $defLine = $lines.Count            # 1-based line of the definition opener
    $lines.Add("    int acc = x;")
    if ($i -gt 0) {
        $lines.Add("    acc += func_$($i-1)(x - 1);")   # the cross-file reference to func_{i-1}
        $callLine = $lines.Count
    }
    for ($l = 0; $l -lt 600; $l++) { $lines.Add("    acc = (acc * 1664525 + 1013904223) ^ (acc >> 3);") }
    $lines.Add("    return acc;"); $lines.Add("}")
    Set-Content $p ($lines -join "`n") -Encoding utf8
    $cInfo.Add([pscustomobject]@{ Index = $i; Path = $p; DefLine = $defLine; CallLine = $callLine })
    if ($BuildOutput) {
        Set-Content ($p -replace '\.c$', '.o')   ([byte[]]::new(64)) -Encoding Byte
        Set-Content ($p -replace '\.c$', '.lst') ("   1 0000 func_${i}:`n   2 0004   push`n") -Encoding ascii
        Set-Content ($p -replace '\.c$', '.bak') ($lines -join "`n") -Encoding utf8
    }
}
# Build the ground-truth manifest: func_i is defined in src_i.c:DefLine and referenced in src_{i+1}.c:CallLine.
foreach ($ci in $cInfo) {
    $sym = "func_$($ci.Index)"
    $refs = @()
    $next = $cInfo | Where-Object { $_.Index -eq ($ci.Index + 1) } | Select-Object -First 1
    if ($next -and $next.CallLine -gt 0) { $refs += "$($next.Path):$($next.CallLine)" }
    $groundTruth[$sym] = @{ def = "$($ci.Path):$($ci.DefLine)"; refs = $refs }
}

Write-Host "  $nCsv csv (tiny-file pressure) ..."
for ($i = 0; $i -lt $nCsv; $i++) {
    $sb = [System.Text.StringBuilder]::new(); [void]$sb.AppendLine("id,name,value,ts")
    for ($r = 0; $r -lt 25; $r++) { [void]$sb.AppendLine("$r,item_$r,$($rng.Next()),2026-09-24") }
    Set-Content (Join-Path (PickDir) "data_$i.csv") $sb.ToString() -Encoding ascii
}

# compile_commands.json (A/B control). none = the firmware default; partial = half the .c; full = all.
if ($CompileDb -ne 'none' -and $cInfo.Count -gt 0) {
    $take = if ($CompileDb -eq 'full') { $cInfo.Count } else { [int]($cInfo.Count / 2) }
    $entries = for ($k = 0; $k -lt $take; $k++) {
        $ci = $cInfo[$k]; $d = Split-Path $ci.Path
        [pscustomobject]@{ directory = $d; file = $ci.Path; command = "clang -I`"$d`" -I`"$outFull`" -c `"$($ci.Path)`"" }
    }
    ($entries | ConvertTo-Json -Depth 4) | Set-Content (Join-Path $outFull "compile_commands.json") -Encoding utf8
    Write-Host "  compile_commands.json: $take/$($cInfo.Count) TUs ($CompileDb)"
}

if ($Manifest) {
    $mpath = Join-Path (Split-Path $outFull) ((Split-Path $outFull -Leaf) + "-manifest.json")
    ($groundTruth | ConvertTo-Json -Depth 5) | Set-Content $mpath -Encoding utf8
    Write-Host "  ground-truth manifest: $mpath ($($groundTruth.Count) symbols)"
}

$stats = Get-ChildItem $outFull -Recurse -File
$total = ($stats | Measure-Object Length -Sum).Sum
Write-Host ("Done: {0:N0} files, {1:N2} GB, {2:N0} dirs, {3} root(s) (compileDb=$CompileDb)." -f $stats.Count, ($total / 1GB), $dirList.Count, $rootsList.Count)

$exe = Join-Path $root "src/CodeCompass.Cli/bin/Release/net8.0/CodeCompass.Cli.exe"
if (($Run -or $Verify) -and -not (Test-Path $exe)) { Write-Host "build the Release CLI first: dotnet build -c Release"; exit 0 }
$ErrorActionPreference = 'Continue' # native CLI writes progress to stderr; don't let it fault a good run

if ($Run) {
    Write-Host "--- indexing ---"; & $exe index $outFull
    $sym = "func_1"
    Write-Host "--- find_references $sym (bounded RSS expected) ---"
    $o = [System.IO.Path]::GetTempFileName()
    $p = Start-Process $exe -ArgumentList @("refs", $outFull, $sym) -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError ([System.IO.Path]::GetTempFileName())
    $peak = 0; $t = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $p.WaitForExit(500)) { try { $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 } } catch { break }; if ($t.Elapsed.TotalSeconds -gt 300) { $p.Kill(); break } }
    "find_references ${sym}: peak RSS {0} MB, {1}s" -f [math]::Round($peak / 1MB), [math]::Round($t.Elapsed.TotalSeconds, 1)
    Get-Content $o | Select-Object -First 5
}

if ($Verify) {
    Write-Host "--- correctness oracle: find_references vs manifest ---"
    & $exe index $outFull 2>&1 | Out-Null
    $syms = @($groundTruth.Keys | Where-Object { $groundTruth[$_].refs.Count -gt 0 } | Sort-Object { $rng.Next() } | Select-Object -First 20)
    $pass = 0; $fail = 0
    foreach ($sym in $syms) {
        $expected = $groundTruth[$sym].refs
        $o = [System.IO.Path]::GetTempFileName()
        $p = Start-Process $exe -ArgumentList @("refs", $outFull, $sym) -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError ([System.IO.Path]::GetTempFileName())
        $null = $p.WaitForExit(60000)
        $out = Get-Content $o -Raw
        $ok = $true
        foreach ($exp in $expected) {
            $parts = $exp -split ':'; $line = $parts[-1]; $file = [System.IO.Path]::GetFileName(($parts[0..($parts.Count-2)] -join ':'))
            if ($out -notmatch [regex]::Escape("$file") -or $out -notmatch ":${line}:") { $ok = $false }
        }
        if ($ok) { $pass++ } else { $fail++; Write-Host "  MISS $sym expected $($expected -join ', ')" -ForegroundColor Yellow }
        Remove-Item $o -Force -ErrorAction SilentlyContinue
    }
    $color = if ($fail -eq 0) { 'Green' } else { 'Red' }
    Write-Host ("Oracle: {0}/{1} symbols resolved their expected cross-file reference." -f $pass, ($pass + $fail)) -ForegroundColor $color
    if ($fail -ne 0) { exit 1 }
}
