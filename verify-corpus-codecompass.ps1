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
$positive = @($syms.PSObject.Properties | Where-Object { @($_.Value.refs).Count -gt 0 } |
    Sort-Object { $rng.Next() } | Select-Object -First $Sample)
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
    Write-Host "--- unresolved-include oracle: gated refs stay unresolved + disclosure fires ---"
    foreach ($prop in $gated) {
        $sym = $prop.Name; $unrefs = @($prop.Value.unreachableRefs)
        $out = Get-Refs $sym
        $leaked = $false
        foreach ($u in $unrefs) {
            $line = ($u -split ':')[-1]; $file = [IO.Path]::GetFileName(($u -replace ':\d+$', ''))
            if ($out -match [regex]::Escape($file) -and $out -match ":${line}:") { $leaked = $true }
        }
        $disclosed = ($out -match 'coverage INCOMPLETE' -and ($out -match 'unresolved' -or $out -match 'VENDOR_missing'))
        if (-not $leaked) { Write-Host "  OK  $sym : $($unrefs.Count) gated ref(s) correctly NOT resolved" -ForegroundColor Green }
        else { $fail++; Write-Host "  LEAK $sym : a gated (unreachable) ref was resolved" -ForegroundColor Red }
        if ($disclosed) { Write-Host "  OK  $sym : find_references disclosed the unresolved include" -ForegroundColor Green }
        else { $fail++; Write-Host "  MISS $sym : no unresolved-include disclosure emitted" -ForegroundColor Red }
    }
    $do = [IO.Path]::GetTempFileName()
    $dp = Start-Process $Cli -ArgumentList @("doctor", $Corpus) -NoNewWindow -PassThru -RedirectStandardOutput $do -RedirectStandardError ([IO.Path]::GetTempFileName())
    $null = $dp.WaitForExit(120000)
    $dout = Get-Content $do -Raw; Remove-Item $do -Force -ErrorAction SilentlyContinue
    if ($dout -match 'translation unit\(s\) reference at least one') { Write-Host "  OK  doctor reported the unresolved-include scan" -ForegroundColor Green }
    else { $fail++; Write-Host "  MISS doctor did not report the unresolved-include scan" -ForegroundColor Red }
}

if ($fail -ne 0) { Write-Host "VERIFY: FAIL ($fail problem(s))" -ForegroundColor Red; exit 1 }
Write-Host "VERIFY: PASS" -ForegroundColor Green
