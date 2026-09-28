<#
.SYNOPSIS
  CodeCompass-side churn helpers for the (upcoming) CodeSpawner `mutate` feature: the `base ⊕ delta`
  symbol-overlay composer and the canonical `prevTruthSha` digest. These implement the contract LOCKED with
  the CodeSpawner session (2026-09-27, docs/mutate-design.md) so our composed truth is byte-for-byte
  identical to the generator's. The verify adapter will dot-source this and drive the churn loop once
  `mutate` ships; until then, `-SelfTest` proves our digest reproduces the agreed golden vector.

  Canonical digest (must match CodeSpawner exactly): sha256 over UTF-8 of, per symbol in ORDINAL name order:
    name 0x1F def 0x1F (refs ordinal-sorted, ","-joined) 0x1F (edges ordinal-sorted, ","-joined) 0x1F
    (expectedMiss?"1":"0") 0x1E        (trailing 0x1E kept; paths verbatim; absent expectedMiss => "0")

.PARAMETER SelfTest
  Run the golden-vector + composition assertions and exit (no mutate binary or corpus needed).
#>
[CmdletBinding()]
param([switch]$SelfTest)

Set-StrictMode -Version Latest

# A "truth" is a hashtable: symbolName -> @{ def=<string>; refs=@(<string>...); edges=@(<string>...); expectedMiss=<bool> }

function Get-TruthDigest {
    param([Parameter(Mandatory)][hashtable]$Symbols)
    $us = [char]0x1f; $rs = [char]0x1e
    $names = [string[]]@($Symbols.Keys)
    [Array]::Sort($names, [System.StringComparer]::Ordinal)
    $sb = [System.Text.StringBuilder]::new()
    foreach ($n in $names) {
        $s = $Symbols[$n]
        $refs  = [string[]]@($s.refs);  [Array]::Sort($refs,  [System.StringComparer]::Ordinal)
        $edges = [string[]]@($s.edges); [Array]::Sort($edges, [System.StringComparer]::Ordinal)
        $em = if ($s.expectedMiss) { "1" } else { "0" }
        [void]$sb.Append($n).Append($us).Append([string]$s.def).Append($us).
            Append(($refs -join ",")).Append($us).Append(($edges -join ",")).Append($us).Append($em).Append($rs)
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    return (($sha.ComputeHash($bytes)) | ForEach-Object { $_.ToString("x2") }) -join ""
}

# Apply a delta (parsed from <corpus>-delta-{k}.json via ConvertFrom-Json) onto a truth, returning a NEW
# truth. Composition rule: tombstone removes; any other entry adds-or-replaces. No ripple special-casing -
# the symbol-keyed overlay already carries every changed symbol (incl. cross-file ref-site ripples).
function Merge-Delta {
    param([Parameter(Mandatory)][hashtable]$Truth, [Parameter(Mandatory)]$Delta)
    $out = @{}
    foreach ($k in $Truth.Keys) { $out[$k] = $Truth[$k] }
    if ($Delta.PSObject.Properties.Name -contains 'symbols' -and $Delta.symbols) {
        foreach ($p in $Delta.symbols.PSObject.Properties) {
            if (($p.Value -is [string]) -and $p.Value -eq 'TOMBSTONE') {
                $out.Remove($p.Name)
            }
            else {
                $em = ($p.Value.PSObject.Properties.Name -contains 'expectedMiss') -and [bool]$p.Value.expectedMiss
                $out[$p.Name] = @{
                    def          = [string]$p.Value.def
                    refs         = @($p.Value.refs)
                    edges        = @($p.Value.edges)
                    expectedMiss = $em
                }
            }
        }
    }
    return $out
}

# Load a manifest's `.symbols` (PSCustomObject from ConvertFrom-Json) into the truth hashtable shape that
# Get-TruthDigest / Merge-Delta use. The composed base truth's digest must equal delta_1's prevTruthSha.
function ConvertTo-Truth {
    param([Parameter(Mandatory)]$ManifestSymbols)
    $t = @{}
    foreach ($p in $ManifestSymbols.PSObject.Properties) {
        $em = ($p.Value.PSObject.Properties.Name -contains 'expectedMiss') -and [bool]$p.Value.expectedMiss
        $t[$p.Name] = @{
            def          = [string]$p.Value.def
            refs         = @($p.Value.refs)
            edges        = @($p.Value.edges)
            expectedMiss = $em
        }
    }
    return $t
}

if ($SelfTest) {
    $ErrorActionPreference = 'Stop'
    $fail = 0
    $ExpectedSha = "7de5e47c16574fd481e461173401dbbe2c874e8712c61049b8830c3a78775d6e"

    # 1) Golden vector: two symbols -> the sha CodeSpawner pinned in the design doc.
    $golden = @{
        "func_0" = @{ def = "block1/src_0.c:11"; refs = @("block1/src_1.c:14"); edges = @();          expectedMiss = $false }
        "func_1" = @{ def = "block1/src_1.c:12"; refs = @();                    edges = @("func_0");   expectedMiss = $false }
    }
    $d = Get-TruthDigest $golden
    if ($d -eq $ExpectedSha) { Write-Host "  OK  golden-vector digest matches ($d)" -ForegroundColor Green }
    else { $fail++; Write-Host "  FAIL golden-vector digest`n        got: $d`n        exp: $ExpectedSha" -ForegroundColor Red }

    # 2) Composition (add): base = {func_0}; delta adds func_1 -> composed digest must equal the golden.
    $base = @{ "func_0" = @{ def = "block1/src_0.c:11"; refs = @("block1/src_1.c:14"); edges = @(); expectedMiss = $false } }
    $addDelta = @'
{ "symbols": { "func_1": { "def": "block1/src_1.c:12", "refs": [], "edges": ["func_0"] } } }
'@ | ConvertFrom-Json
    $composed = Merge-Delta $base $addDelta
    $cd = Get-TruthDigest $composed
    if ($cd -eq $ExpectedSha) { Write-Host "  OK  base + add-delta composes to the golden truth" -ForegroundColor Green }
    else { $fail++; Write-Host "  FAIL add-delta composition got $cd" -ForegroundColor Red }

    # 3) Composition (tombstone): golden + {func_1: TOMBSTONE} -> only func_0 remains.
    $rmDelta = @'
{ "symbols": { "func_1": "TOMBSTONE" } }
'@ | ConvertFrom-Json
    $afterRm = Merge-Delta $golden $rmDelta
    if ($afterRm.Count -eq 1 -and $afterRm.ContainsKey("func_0") -and -not $afterRm.ContainsKey("func_1")) {
        Write-Host "  OK  tombstone-delta removes func_1 (composition drops it)" -ForegroundColor Green
    }
    else { $fail++; Write-Host "  FAIL tombstone-delta left $($afterRm.Count) symbol(s): $($afterRm.Keys -join ',')" -ForegroundColor Red }

    # 4) Order-independence: refs/edges given in reverse must not change the digest (ordinal sort inside).
    $shuffled = @{
        "func_1" = @{ def = "block1/src_1.c:12"; refs = @();                    edges = @("func_0"); expectedMiss = $false }
        "func_0" = @{ def = "block1/src_0.c:11"; refs = @("block1/src_1.c:14"); edges = @();        expectedMiss = $false }
    }
    if ((Get-TruthDigest $shuffled) -eq $ExpectedSha) { Write-Host "  OK  digest is insertion-order independent" -ForegroundColor Green }
    else { $fail++; Write-Host "  FAIL digest changed with insertion order" -ForegroundColor Red }

    if ($fail -eq 0) { Write-Host "CHURN-LIB SELFTEST: PASS" -ForegroundColor Green; exit 0 }
    Write-Host "CHURN-LIB SELFTEST: FAIL ($fail)" -ForegroundColor Red; exit 1
}
