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
.PARAMETER UnresolvedIncludes  Emit N .c TUs that #include a vendor header absent from the tree, each with a
                         reference to vendor_gated() guarded by a macro that only the missing header defines
                         (so the reference is genuinely UNREACHABLE). Exercises doctor's unresolved-include
                         scan and find_references' per-query "includes unresolved" disclosure. Default 0 (off).
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
    [int]$GiantIncluders = 3,
    [int]$TinyFiles = 20586,
    [int]$BlobFiles = 50,
    [int]$MaxHeaderMB = 110,
    [ValidateSet('none', 'partial', 'full')][string]$CompileDb = 'none',
    [bool]$BuildOutput = $true,
    [int]$Dirs = 5700,
    [int]$Depth = 8,
    [int]$LinkedRoots = 1,
    [int]$UnresolvedIncludes = 0,
    [bool]$Manifest = $true,
    [switch]$Run,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$rng = [System.Random]::new($Seed)
$bound = $PSBoundParameters   # which knobs the caller set explicitly

# Effective count for a knob: if the caller passed it EXPLICITLY, use it literally (a one-off run means what
# it says - not silently multiplied by -Scale); otherwise treat the default as a full-repo target and
# multiply by -Scale. A "pathology" knob (keepOne) never rounds to 0 while enabled, so even -Scale 0.001
# keeps one giant header; setting a knob to 0 disables that population.
function Eff([string]$name, [int]$val, [bool]$keepOne = $false) {
    if ($val -le 0) { return 0 }
    if ($bound.ContainsKey($name)) { return $val }
    $n = [int][Math]::Round($val * $Scale)
    if ($keepOne -and $n -lt 1) { $n = 1 }
    return $n
}

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$outFull = (New-Item -ItemType Directory -Force -Path $Out).FullName
Write-Host "Fabricating firmware corpus at $outFull (scale $Scale, seed $Seed, compileDb=$CompileDb) ..."

$nGiant  = Eff 'GiantHeaders'    $GiantHeaders    $true
$nBig    = Eff 'BigHeaders'      $BigHeaders      $false
$nMed    = Eff 'MedHeaders'      $MedHeaders      $false
$nSmallH = Eff 'OrdinaryHeaders' $OrdinaryHeaders $false
$nC      = [Math]::Max(2, (Eff 'CFiles' $CFiles $false))   # need >=2 for a cross-file reference edge
$nCsv    = Eff 'TinyFiles'       $TinyFiles       $false
$nBlob   = Eff 'BlobFiles'       $BlobFiles       $false

# Directory tree: root plus LinkedRoots-1 sibling trees, each block/sub/mod, up to ~Depth deep.
$rootsList = New-Object System.Collections.Generic.List[string]
$rootsList.Add($outFull)
for ($r = 1; $r -lt [Math]::Max(1, $LinkedRoots); $r++) {
    $rootsList.Add((New-Item -ItemType Directory -Force -Path (Join-Path (Split-Path $outFull) ((Split-Path $outFull -Leaf) + "_root$r"))).FullName)
}
$dirList = New-Object System.Collections.Generic.List[string]
$perRoot = [Math]::Max(1, [int][Math]::Round((Eff 'Dirs' $Dirs $false) / $rootsList.Count / 9))
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
    # A 1 MB StreamWriter buffer + BATCHED StringBuilder chunks (~5 MB) written in one call each. Per-line
    # WriteLine is the bottleneck at scale (~1M calls per 110 MB header); batching cuts write calls ~20,000x,
    # so even the 1.44 GB max header generates in seconds instead of minutes.
    $sw = [System.IO.StreamWriter]::new($path, $false, [System.Text.Encoding]::ASCII, 1 * 1024 * 1024)
    try {
        $sw.Write("#ifndef $guard`n#define $guard`n/* generated hardware register map - block $fam (synthetic) */`n")
        $reg = 0; $emitted = 0
        $sb = [System.Text.StringBuilder]::new(6 * 1024 * 1024)
        $batchRegs = 16384  # registers per flush (~48k lines, ~5 MB)
        while ($emitted -lt $defines -and $sw.BaseStream.Length -lt $maxBytes) {
            [void]$sb.Clear()
            for ($k = 0; $k -lt $batchRegs -and $emitted -lt $defines; $k++) {
                $addr = ($reg * 4).ToString("x8")
                [void]$sb.Append("#define HWIO_BLK${fam}_REG${reg}_ADDR (BASE_BLOCK$fam + 0x$addr)`n#define HWIO_BLK${fam}_REG${reg}_RMSK 0x000000ff`n#define HWIO_BLK${fam}_REG${reg}_IN in_dword(HWIO_BLK${fam}_REG${reg}_ADDR)`n")
                $reg++; $emitted += 3
            }
            $sw.Write($sb.ToString())
        }
        $sw.Write("#endif`n")
    } finally { $sw.Close() }
}

# Giants fill to -MaxHeaderMB by default (byte shape drives "bigger repos"); -MacroDensity, when explicitly
# set, caps the define count instead - the pure "many macros, few bytes" memory-axis test (pair with a small
# -MaxHeaderMB). Either way a giant carries ~1M+ #defines, which is the preprocessor stressor.
$giantDefineCap = if ($PSBoundParameters.ContainsKey('MacroDensity')) { $MacroDensity } else { [int]::MaxValue }
Write-Host "  register headers: $nGiant giant (<=${MaxHeaderMB}MB, densityCap $giantDefineCap) + $nBig big + $nMed medium ..."
$giantPaths = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $nGiant; $i++) {
    $p = Join-Path (PickDir) "regmap_block$i.h"
    New-RegHeader $p $giantDefineCap ([long]$MaxHeaderMB * 1MB) $i
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
# another dir). Ground-truth def/ref sites are recorded as we emit, for the correctness oracle.
#
# GIANT-INCLUDE STRESS: the first $GiantIncluders .c files are CO-LOCATED with a giant header and #include
# it (co-location guarantees the include resolves via -I<dir>, so clang actually ingests the ~1M macros -
# random dirs would silently fail to resolve and test nothing). Each also calls a shared HOT SYMBOL
# (hot_shared), so find_references(hot_shared) must parse EVERY giant-including TU in one query - the
# aggregate-memory test: does per-TU dispose keep peak flat across N giant TUs, or does it creep?
$groundTruth = @{}
$cInfo = New-Object System.Collections.Generic.List[object]
$nGiantInc = if ($giantPaths.Count -gt 0) { [Math]::Min($GiantIncluders, $nC) } else { 0 }

# hot_shared: defined once (cheap file, no giant), called from every giant-including .c.
$hotPath = Join-Path (PickDir) "hot_shared.c"
Set-Content $hotPath "int hot_shared(int x) { return x + 1; }" -Encoding utf8
$hotRefs = New-Object System.Collections.Generic.List[string]

for ($i = 0; $i -lt $nC; $i++) {
    $isInc = $i -lt $nGiantInc
    if ($isInc) { $giant = $giantPaths[$i % $giantPaths.Count]; $dir = Split-Path $giant } else { $dir = PickDir }
    $p = Join-Path $dir "src_$i.c"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("#include <stddef.h>")
    for ($inc = 0; $inc -lt 6; $inc++) { if ($nSmallH -gt 0) { $lines.Add("#include ""hdr_$($rng.Next(0,$nSmallH)).h""") } }
    if ($isInc) { $lines.Add("#include ""$([System.IO.Path]::GetFileName($giant))""") } # co-located -> resolves
    $lines.Add("int func_$i(int x);")
    if ($isInc) { $lines.Add("int hot_shared(int x);") }
    if ($i -gt 0) { $lines.Add("int func_$($i-1)(int x);") }
    $lines.Add("int func_$i(int x) {")
    $defLine = $lines.Count            # 1-based line of the definition opener
    $lines.Add("    int acc = x;")
    $callLine = 0
    if ($i -gt 0) {
        $lines.Add("    acc += func_$($i-1)(x - 1);")   # the cross-file reference to func_{i-1}
        $callLine = $lines.Count
    }
    if ($isInc) { $lines.Add("    acc += hot_shared(x);"); $hotRefs.Add("${p}:$($lines.Count)") } # aggregate ref
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
# Ground-truth manifest: func_i defined in src_i.c:DefLine, referenced in src_{i+1}.c:CallLine; hot_shared
# defined in hot_shared.c and referenced from every giant-including .c (the aggregate query's expected set).
foreach ($ci in $cInfo) {
    $sym = "func_$($ci.Index)"
    $refs = @()
    $next = $cInfo | Where-Object { $_.Index -eq ($ci.Index + 1) } | Select-Object -First 1
    if ($next -and $next.CallLine -gt 0) { $refs += "$($next.Path):$($next.CallLine)" }
    $groundTruth[$sym] = @{ def = "$($ci.Path):$($ci.DefLine)"; refs = $refs }
}
if ($nGiantInc -gt 0) { $groundTruth["hot_shared"] = @{ def = "${hotPath}:1"; refs = @($hotRefs) } }
Write-Host "  giant-include stress: $nGiantInc .c co-located with + including a giant header, all calling hot_shared()"

# UNRESOLVED-INCLUDE STRESS: N .c TUs each #include a vendor header that exists NOWHERE in the tree. Each holds
# a reference to vendor_gated() inside #ifdef VENDOR_OK - and VENDOR_OK is defined ONLY by that missing header -
# so the reference is preprocessed out and is genuinely UNREACHABLE without the header. This is the honest
# negative case: doctor's unresolved-include scan must count these TUs, find_references' per-query disclosure
# must name the missing headers, and the oracle asserts the gated refs are NOT (and should not be) resolved.
$nUnres = if ($UnresolvedIncludes -gt 0) { [Math]::Min($UnresolvedIncludes, [Math]::Max(1, $nC)) } else { 0 }
if ($nUnres -gt 0) {
    $vgPath = Join-Path (PickDir) "vendor_gated.c"
    Set-Content $vgPath "int vendor_gated(int x) { return x + 2; }" -Encoding utf8
    $unreach = New-Object System.Collections.Generic.List[string]
    for ($k = 0; $k -lt $nUnres; $k++) {
        $up = Join-Path (PickDir) "unres_$k.c"
        $ul = New-Object System.Collections.Generic.List[string]
        $ul.Add("#include ""VENDOR_missing_$k.h""")   # never created anywhere -> unresolvable
        $ul.Add("int vendor_gated(int x);")
        $ul.Add("#ifdef VENDOR_OK")                    # VENDOR_OK is defined only by the missing header
        $ul.Add("int use_vendor_$k(int x) {")
        $ul.Add("    return vendor_gated(x + $k);")     # UNREACHABLE ref (macro undefined -> block dropped)
        $unreach.Add("${up}:$($ul.Count)")
        $ul.Add("}")
        $ul.Add("#endif")
        Set-Content $up ($ul -join "`n") -Encoding utf8
    }
    # vendor_gated: defined once; every listed ref is expected UNREACHABLE (empty reachable set).
    $groundTruth["vendor_gated"] = @{ def = "${vgPath}:1"; refs = @(); unreachableRefs = @($unreach) }
    Write-Host "  unresolved-include stress: $nUnres .c #include a missing vendor header; $($unreach.Count) UNREACHABLE ref(s) to vendor_gated()"
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
    Write-Host "--- indexing ---"
    $idxlog = [System.IO.Path]::GetTempFileName()
    & $exe index $outFull 2>&1 | Out-File $idxlog
    Get-Content $idxlog | Select-String 'Indexed |Throughput|Trigram' | ForEach-Object { $_.Line }
    Remove-Item $idxlog -Force -ErrorAction SilentlyContinue
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

    # Negative + disclosure oracle: symbols whose refs are all UNREACHABLE (behind a macro only a missing
    # header defines). find_references must NOT resolve them, and must DISCLOSE the unresolved include.
    $gated = @($groundTruth.Keys | Where-Object { $groundTruth[$_].unreachableRefs -and $groundTruth[$_].unreachableRefs.Count -gt 0 })
    if ($gated.Count -gt 0) {
        Write-Host "--- unresolved-include oracle: unreachable refs stay unresolved + disclosure fires ---"
        foreach ($sym in $gated) {
            $unrefs = $groundTruth[$sym].unreachableRefs
            $o = [System.IO.Path]::GetTempFileName(); $e = [System.IO.Path]::GetTempFileName()
            $p = Start-Process $exe -ArgumentList @("refs", $outFull, $sym) -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
            $null = $p.WaitForExit(60000)
            $out = Get-Content $o -Raw; $err = Get-Content $e -Raw
            $leaked = $false
            foreach ($u in $unrefs) {
                $parts = $u -split ':'; $line = $parts[-1]; $file = [System.IO.Path]::GetFileName(($parts[0..($parts.Count-2)] -join ':'))
                if ($out -match [regex]::Escape("$file") -and $out -match ":${line}:") { $leaked = $true }
            }
            $disclosed = ($err -match 'unresolved' -or $out -match 'unresolved' -or $err -match 'VENDOR_missing' -or $out -match 'VENDOR_missing')
            if (-not $leaked) { Write-Host "  OK  $sym : $($unrefs.Count) gated ref(s) correctly NOT resolved" -ForegroundColor Green }
            else { $fail++; Write-Host "  LEAK $sym : a gated (unreachable) ref was resolved" -ForegroundColor Red }
            if ($disclosed) { Write-Host "  OK  $sym : find_references disclosed the unresolved include" -ForegroundColor Green }
            else { $fail++; Write-Host "  MISS $sym : no unresolved-include disclosure emitted" -ForegroundColor Red }
            Remove-Item $o, $e -Force -ErrorAction SilentlyContinue
        }
        # doctor should proactively report the unresolved-include gap.
        $do = [System.IO.Path]::GetTempFileName()
        $dp = Start-Process $exe -ArgumentList @("doctor", $outFull) -NoNewWindow -PassThru -RedirectStandardOutput $do -RedirectStandardError ([System.IO.Path]::GetTempFileName())
        $null = $dp.WaitForExit(120000)
        $dout = Get-Content $do -Raw
        if ($dout -match 'translation unit\(s\) reference at least one') { Write-Host "  OK  doctor reported the unresolved-include scan" -ForegroundColor Green }
        else { $fail++; Write-Host "  MISS doctor did not report the unresolved-include scan" -ForegroundColor Red }
        Remove-Item $do -Force -ErrorAction SilentlyContinue
    }

    if ($fail -ne 0) { exit 1 }
}
