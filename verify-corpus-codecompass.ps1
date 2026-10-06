<#
.SYNOPSIS
  CodeCompass-side verify adapter for a CodeSpawner v1 corpus. Indexes the corpus and asserts
  find_references matches the ground-truth manifest (positive + negative + disclosure + doctor).

.DESCRIPTION
  The synthetic generator lives in its own project now (CodeSpawner). This adapter is the CodeCompass
  half of the split: the generator emits corpus + manifest; THIS drives CodeCompass's own verification.
  It consumes the versioned v1 manifest (docs/manifest-schema.md): `_meta` + `symbols` with repo-relative
  paths, `edges`, and the negative `unreachableRefs` set. It asserts `_meta.manifestVersion == 1` up front
  so a schema change is a one-line failure, not a mysterious miss.

  Checks:
   1. POSITIVE: for a sample of symbols, find_references returns each expected ref site (basename + :line:).
   2. NEGATIVE: for gated symbols (unreachableRefs), those sites are NOT resolved.
   3. DISCLOSURE: find_references prints the unresolved-include caveat on STDOUT.
   4. DOCTOR: reports the unresolved-include scan.

.PARAMETER Corpus    The generated corpus dir.
.PARAMETER Manifest  Manifest path (default: <corpus>-manifest.json sibling).
.PARAMETER Sample    How many positive symbols to sample (default 20).
.PARAMETER Cli       Path to CodeCompass.Cli.exe (default: Release build).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Corpus,
    [string]$Manifest,
    [int]$Sample = 20,
    [string]$Cli
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Cli) { $Cli = Join-Path $root "src\CodeCompass.Cli\bin\Release\net10.0\CodeCompass.Cli.exe" }
if (-not (Test-Path $Cli)) { throw "build the Release CLI first: dotnet build -c Release" }
if (-not (Test-Path $Corpus)) { throw "corpus not found: $Corpus" }
$Corpus = (Resolve-Path $Corpus).Path
if (-not $Manifest) { $Manifest = (Split-Path $Corpus) + [IO.Path]::DirectorySeparatorChar + (Split-Path $Corpus -Leaf) + "-manifest.json" }
if (-not (Test-Path $Manifest)) { throw "manifest not found: $Manifest" }

$m = Get-Content $Manifest -Raw | ConvertFrom-Json

# --- assert the contract version BEFORE trusting anything else ---
$ver = $m._meta.manifestVersion
if ($ver -ne 1) { throw "manifest version $ver != 1 - this adapter speaks v1 (see tools\codespawner\manifest-schema.md)" }
Write-Host ("manifest v{0}, seed {1}, generator {2}" -f $ver, $m._meta.seed, $m._meta.generatorVersion) -ForegroundColor Cyan

$syms = $m.symbols
$rng = [System.Random]::new([int]$m._meta.seed)

$ErrorActionPreference = 'Continue'  # native CLI writes progress to stderr; don't fault a good run

Write-Host "--- indexing $Corpus ---"
& $Cli index $Corpus 2>&1 | Out-Null

# Helper: run refs and return stdout text.
function Get-Refs([string]$sym) {
    $o = [IO.Path]::GetTempFileName(); $e = [IO.Path]::GetTempFileName()
    $p = Start-Process $Cli -ArgumentList @("refs", $Corpus, $sym) -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
    $null = $p.WaitForExit(60000)
    $out = Get-Content $o -Raw; Remove-Item $o, $e -Force -ErrorAction SilentlyContinue
    return $out
}

# --- POSITIVE oracle ---
# Exclude expectedMiss symbols (token-paste names a lexical indexer is SUPPOSED to miss) - they have their
# own oracle below; sampling them as positives would flag a correct honest-miss as a recall failure.
$positive = @($syms.PSObject.Properties | Where-Object {
        @($_.Value.refs).Count -gt 0 -and
        -not (($_.Value.PSObject.Properties.Name -contains 'expectedMiss') -and $_.Value.expectedMiss)
    } | Sort-Object { $rng.Next() } | Select-Object -First $Sample)
$pass = 0; $fail = 0
foreach ($prop in $positive) {
    $sym = $prop.Name; $expected = @($prop.Value.refs)
    $out = Get-Refs $sym
    $ok = $true
    foreach ($exp in $expected) {
        $line = ($exp -split ':')[-1]
        $file = [IO.Path]::GetFileName(($exp -replace ':\d+$', ''))
        if ($out -notmatch [regex]::Escape($file) -or $out -notmatch ":${line}:") { $ok = $false }
    }
    if ($ok) { $pass++ } else { $fail++; Write-Host "  MISS $sym expected $($expected -join ', ')" -ForegroundColor Yellow }
}
$col = if ($fail -eq 0) { 'Green' } else { 'Red' }
Write-Host ("POSITIVE: {0}/{1} symbols resolved their expected cross-file reference." -f $pass, ($pass + $fail)) -ForegroundColor $col

# --- NEGATIVE + DISCLOSURE oracle ---
# NB: a PSCustomObject returns $null for an ABSENT property, and @($null).Count is 1 (not 0) - so we must
# check the property actually exists before counting, or every symbol looks "gated".
$gated = @($syms.PSObject.Properties | Where-Object {
    ($_.Value.PSObject.Properties.Name -contains 'unreachableRefs') -and @($_.Value.unreachableRefs).Count -gt 0
})
if ($gated.Count -gt 0) {
    # C/C++ references are a NAME search (no compiler): a use behind a missing header or an inactive #ifdef is still a
    # use of the name, so it IS listed, and the answer says C/C++ is matched by name. (The clang-era oracle asserted the
    # opposite: gated refs unresolved + an "unresolved #include" disclosure + a doctor include scan, all removed in v1.0.239.)
    Write-Host "--- gated-refs oracle: uses behind missing headers / #ifdef are found by name + the by-name note fires ---"
    foreach ($prop in $gated) {
        $sym = $prop.Name; $unrefs = @($prop.Value.unreachableRefs)
        $out = Get-Refs $sym
        $missing = 0
        foreach ($u in $unrefs) {
            $line = ($u -split ':')[-1]; $file = [IO.Path]::GetFileName(($u -replace ':\d+$', ''))
            if (-not ($out -match ([regex]::Escape($file) + ":${line}:"))) { $missing++ }
        }
        if ($missing -eq 0) { Write-Host "  OK  $sym : $($unrefs.Count) gated ref(s) found by name" -ForegroundColor Green }
        else { $fail++; Write-Host "  MISS $sym : $missing of $($unrefs.Count) gated ref(s) not listed" -ForegroundColor Red }
        if ($out -match 'matched by NAME') { Write-Host "  OK  $sym : the answer says C/C++ is matched by name" -ForegroundColor Green }
        else { $fail++; Write-Host "  MISS $sym : no matched-by-name note" -ForegroundColor Red }
    }
}

# --- EXPECTED-MISS oracle (honest-miss dual of unreachableRefs) ---
# Symbols with expectedMiss=true are token-paste (##) macro names that never appear literally in the
# source, so a lexical / preprocessor-blind indexer is EXPECTED to return no def/ref site. Finding one
# would be a false positive (the indexer inventing a symbol that isn't textually there).
$expMiss = @($syms.PSObject.Properties | Where-Object {
        ($_.Value.PSObject.Properties.Name -contains 'expectedMiss') -and $_.Value.expectedMiss } |
    Sort-Object { $rng.Next() } | Select-Object -First $Sample)  # sample like positives - one CLI call each
if ($expMiss.Count -gt 0) {
    Write-Host "--- expected-miss oracle: token-paste (##) names must NOT resolve (sampled $($expMiss.Count)) ---"
    foreach ($prop in $expMiss) {
        $sym = $prop.Name
        $out = Get-Refs $sym
        # A resolved hit prints a path:line:col location. Its ABSENCE is the correct (honest-miss) result.
        if ($out -match ':\d+:\d+:') {
            $fail++; Write-Host "  LEAK $sym : an expected-miss token-paste name resolved to a location" -ForegroundColor Red
        }
        else { Write-Host "  OK  $sym : correctly NOT resolved (expected miss)" -ForegroundColor Green }
    }
}

# --- DUP-CONTENT oracle: byte-identical copies must each stay independently searchable ---
# Content-hash dedup is for change detection, not for collapsing search results: every byte-identical
# file is its own document. A distinctive identifier from one copy must find ALL copies in the group.
if (($m.PSObject.Properties.Name -contains 'dupGroups') -and $m.dupGroups) {
    Write-Host "--- dup-content oracle: every identical copy is indexed (dedup must not drop files) ---"
    $dupSample = @($m.dupGroups.PSObject.Properties | Sort-Object { $rng.Next() } | Select-Object -First $Sample)
    foreach ($grp in $dupSample) {
        $paths = @($grp.Value.paths)
        if ($paths.Count -lt 2) { continue }
        $first = Join-Path $Corpus ($paths[0] -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $first)) { $fail++; Write-Host "  MISS dup file absent on disk: $($paths[0])" -ForegroundColor Red; continue }
        # Prefer a full identifier that embeds the group name (e.g. dup0 -> dup0_seed) - guaranteed
        # group-distinctive, so results aren't a broad token spanning the corpus; fall back to any
        # long-ish identifier. Scan identifiers on each line and pick the first containing the name.
        $tok = $null
        foreach ($ln in (Get-Content $first -TotalCount 300)) {
            foreach ($id in [regex]::Matches($ln, '[A-Za-z_][A-Za-z0-9_]*')) {
                if ($id.Value -like "*$($grp.Name)*") { $tok = $id.Value; break }
            }
            if ($tok) { break }
        }
        if (-not $tok) {
            foreach ($ln in (Get-Content $first -TotalCount 300)) {
                $mt = [regex]::Match($ln, '[A-Za-z_][A-Za-z0-9_]{11,}')
                if ($mt.Success) { $tok = $mt.Value; break }
            }
        }
        if (-not $tok) { Write-Host "  SKIP $($grp.Name): no distinctive token to search" -ForegroundColor Yellow; continue }
        $out = & $Cli search $Corpus $tok 2>$null | Out-String
        $missing = @($paths | Where-Object { $out -notmatch [regex]::Escape([IO.Path]::GetFileName($_)) })
        if ($missing.Count -eq 0) { Write-Host "  OK  $($grp.Name): all $($paths.Count) identical copies searchable via '$tok'" -ForegroundColor Green }
        else { $fail++; Write-Host "  MISS $($grp.Name): $($missing.Count)/$($paths.Count) copies not found for '$tok' (dedup dropped a file, or results were capped)" -ForegroundColor Red }
    }
}

if ($fail -ne 0) { Write-Host "VERIFY: FAIL ($fail problem(s))" -ForegroundColor Red; exit 1 }
Write-Host "VERIFY: PASS" -ForegroundColor Green
