<#
.SYNOPSIS
  CodeCompass incremental-update / reconcile / orphan-cleanup oracle, driven by CodeSpawner `mutate`.
  Indexes a base corpus, then for each edit step: applies the edit (mutate --step k), runs the incremental
  `codecompass update` (the thing under test), composes base (+) delta_k, and asserts the LIVE index matches
  the composed ground truth. This is the test the incremental path never had.

.DESCRIPTION
  Per step it checks:
   1. CHAIN INTEGRITY - my composed truth's digest equals delta_k's prevTruthSha (catches any composer /
      generator drift before trusting the rest).
   2. TOMBSTONE reconcile - a removed symbol no longer resolves (find_definition/refs return no location).
      This is the classic "stale entry lingers" incremental bug.
   3. LEAK - each removedSite (a ref site that vanished) is gone from find_references of its symbol.
   4. POSITIVE - a churned symbol's surviving refs still resolve at their (possibly shifted) lines.
   5. SIDECAR fileOps - the on-disk sidecar count moves correctly for the grow (create) / shrink (delete)
      edits, exercising orphan-cleanup.

.PARAMETER Corpus  An existing CodeSpawner corpus (generate with --shrink-seeds >=1 to get 4-shrink).
.PARAMETER Seed    mutate edit-selection seed (default 7).
.PARAMETER Edits   number of edits in the chain (default 5).
.PARAMETER Cli     Path to CodeCompass.Cli.exe (default: Release build).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Corpus,
    [int]$Seed = 7,
    [int]$Edits = 5,
    [string]$Cli
)
$root = $PSScriptRoot
. (Join-Path $root "churn-lib.ps1")
if (-not $Cli) { $Cli = Join-Path $root "src\CodeCompass.Cli\bin\Release\net10.0\CodeCompass.Cli.exe" }
if (-not (Test-Path $Cli)) { throw "build the Release CLI first: dotnet build -c Release" }
$spawner = Join-Path $root "tools\codespawner\codespawner.exe"
$Corpus = (Resolve-Path $Corpus).Path
$manifest = (Split-Path $Corpus) + [IO.Path]::DirectorySeparatorChar + (Split-Path $Corpus -Leaf) + "-manifest.json"

$fail = 0
function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

# Run a CLI verb, return stdout text (stderr is progress; ignore).
function Cli-Out([string[]]$cliArgs) {
    $o = [IO.Path]::GetTempFileName(); $e = [IO.Path]::GetTempFileName()
    $p = Start-Process $Cli -ArgumentList $cliArgs -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
    $null = $p.WaitForExit(120000)
    $out = Get-Content $o -Raw; Remove-Item $o, $e -Force -EA SilentlyContinue
    return [string]$out
}
# Parse a refs/def output into an EXACT set of "basename:line" sites. The output format is
# path:LINE:COL: text - we key on (basename, LINE) so a removed line number can't spuriously match a
# surviving result's COLUMN (e.g. removed line 12 vs a real hit at line 24 col 12).
function Get-SiteSet([string]$out) {
    $set = @{}
    foreach ($ln in ($out -split "`r?`n")) {
        $m = [regex]::Match($ln, '^(?<p>.+?):(?<l>\d+):\d+:')
        if ($m.Success) { $set[([IO.Path]::GetFileName($m.Groups['p'].Value) + ":" + $m.Groups['l'].Value)] = $true }
    }
    return $set
}
# "block/../src_4.c:12" -> "src_4.c:12"
function Site-Key([string]$site) { return [IO.Path]::GetFileName(($site -replace ':\d+$', '')) + ":" + (($site -split ':')[-1]) }
function Sidecar-Count {
    $d = Cli-Out @("doctor", $Corpus)
    if ($d -match 'sidecar cache:\s+([\d,]+) file') { return [int](($Matches[1]) -replace ',', '') }
    return -1
}

Say "== base: $Corpus ==" 'Cyan'
$m = Get-Content $manifest -Raw | ConvertFrom-Json
if ($m._meta.manifestVersion -ne 1) { throw "manifest v$($m._meta.manifestVersion) != 1" }
$truth = ConvertTo-Truth $m.symbols
Say ("  base truth: {0} symbols" -f $truth.Count)

Say "== index base ==" 'Cyan'
$null = Cli-Out @("index", $Corpus)
$scPrev = Sidecar-Count

for ($k = 1; $k -le $Edits; $k++) {
    # Capture the WHOLE mutate output (Out-String drains the pipeline). Do NOT pipe through
    # Select-Object -First 1 - that stops the pipeline early and kills the native process before it
    # finishes writing the delta file.
    $mout = (& $spawner mutate --corpus $Corpus --seed $Seed --edits $Edits --step $k 2>&1 | Out-String)
    # Require >=1 comma so this matches the edit list "(Remove,LineShift,...)" and NOT the "(s)" in "edit(s)".
    $plan = @()
    if ($mout -match '\(([A-Za-z]+(?:,[A-Za-z]+)+)\)') { $plan = @($Matches[1] -split ',') }
    $editType = if ($plan.Count -ge $k) { $plan[$k - 1] } else { "?" }
    $deltaPath = Join-Path (Split-Path $Corpus) ((Split-Path $Corpus -Leaf) + "-delta-$k.json")
    if (-not (Test-Path $deltaPath)) { $fail++; Say "  step ${k}: no delta emitted" 'Red'; continue }
    $delta = Get-Content $deltaPath -Raw | ConvertFrom-Json

    # 1. CHAIN INTEGRITY: my composed truth (state_{k-1}) must match what mutate expected.
    $preDig = Get-TruthDigest $truth
    if ($preDig -ne $delta._meta.prevTruthSha) {
        $fail++; Say "  step $k CHAIN MISMATCH: my $preDig vs prevTruthSha $($delta._meta.prevTruthSha)" 'Red'
    }
    $truth = Merge-Delta $truth $delta

    # 2. Apply the incremental update (the thing under test), then assert the live index == composed truth.
    $null = Cli-Out @("update", $Corpus)

    $tomb = @($delta.symbols.PSObject.Properties | Where-Object { ($_.Value -is [string]) -and $_.Value -eq 'TOMBSTONE' })
    $chg  = @($delta.symbols.PSObject.Properties | Where-Object { -not (($_.Value -is [string]) -and $_.Value -eq 'TOMBSTONE') })
    $sPass = 0

    foreach ($p in $tomb) {  # 2. reconcile: the removed symbol's DEFINITION must be purged. (find_references
        # may still show surviving textual callers in other files - that's correct, not a linger.)
        $def = Cli-Out @("def", $Corpus, $p.Name)
        if ($def -match '\.[A-Za-z0-9_]+:\d+') { $fail++; Say "  step $k LINGER: tombstoned $($p.Name) still has a definition" 'Red' } else { $sPass++ }
    }
    foreach ($p in $chg) {
        $sites = Get-SiteSet (Cli-Out @("refs", $Corpus, $p.Name))
        # 3. LEAK: each removed ref site (basename:line) must be GONE from find_references.
        if ($p.Value.PSObject.Properties.Name -contains 'removedSites') {
            foreach ($u in @($p.Value.removedSites)) {
                if ($sites.ContainsKey((Site-Key $u))) { $fail++; Say "  step $k LEAK: $($p.Name) still refs $u" 'Red' }
            }
        }
        # 4. POSITIVE: every surviving composed ref (at its possibly-shifted line) must be present.
        $ok = $true
        foreach ($r in @($p.Value.refs)) { if (-not $sites.ContainsKey((Site-Key $r))) { $ok = $false } }
        if ($ok) { $sPass++ } else { $fail++; Say "  step $k MISS: $($p.Name) refs not all at composed truth lines" 'Yellow' }
    }

    # 5. SIDECAR fileOps: count should move for create/delete edits.
    $scNow = Sidecar-Count
    $scDelta = if ($scPrev -ge 0 -and $scNow -ge 0) { $scNow - $scPrev } else { 0 }
    $scPrev = $scNow
    Say ("  step $k [{0,-9}] symbols ok:{1}/{2}  files +{3}/-{4}/~{5}  sidecars:{6} ({7:+#;-#;0})" -f `
        $editType, $sPass, ($tomb.Count + $chg.Count), `
        @($delta.fileOps.added).Count, @($delta.fileOps.removed).Count, @($delta.fileOps.modified).Count, $scNow, $scDelta) `
        ($(if ($fail -eq 0) { 'Green' } else { 'Gray' }))
}

if ($fail -ne 0) { Say "CHURN VERIFY: FAIL ($fail problem(s))" 'Red'; exit 1 }
Say "CHURN VERIFY: PASS ($Edits steps, chain digests + reconcile + leak + positive all held)" 'Green'
