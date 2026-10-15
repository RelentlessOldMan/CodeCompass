<#
.SYNOPSIS
  CodeCompass-side verify adapter for a CodeSpawner C++ corpus (`gen --lang cpp`, manifest v2).
  Indexes the corpus and checks find_definition + find_references against the ground truth, per simple name.

.DESCRIPTION
  C/C++ references are a whole-word NAME search, so same-named symbols are answered together. The manifest's
  truth for one simple name is the union of every symbol with that simpleName: its def line plus its ref lines
  (a class and its ctor share one; so do a template and its instances, and every override of a method). For
  each simple name this compares, by file:line:
   - RECALL: every truth line is in CodeCompass's answer (refs lines plus def start lines). A miss fails.
   - PRECISION: every line CodeCompass lists is in the truth. The manifest records in-class declarations of
     out-of-class bodies (and prototypes) as neither def nor ref, but a name search can't tell a declaration
     from a use, so an extra line that IS such a declaration is counted as expected, not a failure. Any other
     extra line fails.
   - DEFS: every def CodeCompass reports is a truth def, and every truth def is reported - except a pure
     virtual (`pure: true`): its def is the `= 0;` declaration, which has no body, and CodeCompass takes
     definitions only from bodies (its line is still found by refs).
  The v1 C adapter (verify-corpus-codecompass.ps1) keeps its hard manifestVersion == 1 assert; this one
  asserts version 2 and lang cpp (see tools\codespawner\manifest-schema.md).

.PARAMETER Corpus    The generated corpus dir.
.PARAMETER Manifest  Manifest path (default: <corpus>-manifest.json sibling).
.PARAMETER Cli       Path to CodeCompass.Cli.exe (default: Release build).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Corpus,
    [string]$Manifest,
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
if ($ver -ne 2) { throw "manifest version $ver != 2 - this adapter speaks the C++ v2 manifest (C corpora: verify-corpus-codecompass.ps1)" }
if ($m._meta.lang -ne 'cpp') { throw "manifest lang '$($m._meta.lang)' != cpp" }
Write-Host ("manifest v{0} (cpp), seed {1}, generator {2}" -f $ver, $m._meta.seed, $m._meta.generatorVersion) -ForegroundColor Cyan

$ErrorActionPreference = 'Continue'  # native CLI writes progress to stderr; don't fault a good run

Write-Host "--- indexing $Corpus ---"
& $Cli index $Corpus 2>&1 | Out-Null

# Group the truth by simple name: def lines, ref lines, and pure-virtual defs.
$groups = @{}
foreach ($p in $m.symbols.PSObject.Properties) {
    $s = $p.Value
    $name = $s.simpleName
    if (-not $groups.ContainsKey($name)) {
        $groups[$name] = @{ Defs = [Collections.Generic.HashSet[string]]::new(); PureDefs = [Collections.Generic.HashSet[string]]::new()
                            Refs = [Collections.Generic.HashSet[string]]::new(); Keys = [Collections.Generic.List[string]]::new() }
    }
    $g = $groups[$name]
    $g.Keys.Add($p.Name)
    if ($s.def) {
        if (($s.PSObject.Properties.Name -contains 'pure') -and $s.pure) { [void]$g.PureDefs.Add($s.def) } else { [void]$g.Defs.Add($s.def) }
    }
    foreach ($r in @($s.refs)) { if ($r) { [void]$g.Refs.Add($r) } }
}

# Run the CLI and return "path:line" for each result line (refs: path:line:col: text; def: path:start-end:col: Kind Name).
function Get-Lines([string]$cmd, [string]$name) {
    $set = [Collections.Generic.HashSet[string]]::new()
    foreach ($l in (& $Cli $cmd $Corpus $name 2>$null)) {
        $mt = [regex]::Match($l, '^(?<path>[^:]+(?::[\\/][^:]+)?):(?<line>\d+)(?:-\d+)?:\d+:')
        if ($mt.Success) { [void]$set.Add(($mt.Groups['path'].Value -replace '\\', '/') + ':' + $mt.Groups['line'].Value) }
    }
    return , $set
}

# A declaration of a function with no body: `[virtual] type name(params) [const] [override] [final] [= 0];`
# (params may nest one level, as a member-function-pointer parameter does: `void mp_run_X(void (ns::X::*f)());`).
# The manifest records it as neither def nor ref; a name search lists it like any other use.
function Test-Declaration([string]$site, [string]$name) {
    $i = $site.LastIndexOf(':'); $file = Join-Path $Corpus $site.Substring(0, $i); $ln = [int]$site.Substring($i + 1)
    $text = (Get-Content $file -TotalCount $ln)[-1]
    $n = [regex]::Escape($name)
    return $text -match "^\s*(?:virtual\s+|static\s+|explicit\s+|inline\s+)*(?:[\w:<>,\s\*&]+\s+)?~?$n\s*\((?:[^()]|\([^()]*\))*\)\s*(?:const\s*)?(?:noexcept\s*)?(?:override\s*)?(?:final\s*)?(?:=\s*(?:0|default|delete)\s*)?;\s*$"
}

$fail = 0; $names = 0; $truthLines = 0; $declExtras = 0; $pureGaps = 0
foreach ($name in ($groups.Keys | Sort-Object)) {
    $g = $groups[$name]; $names++
    $truth = [Collections.Generic.HashSet[string]]::new($g.Refs)
    $truth.UnionWith($g.Defs); $truth.UnionWith($g.PureDefs)
    $truthLines += $truth.Count

    $refs = Get-Lines 'refs' $name
    $defs = Get-Lines 'def' $name
    $got = [Collections.Generic.HashSet[string]]::new($refs); $got.UnionWith($defs)

    $problems = [Collections.Generic.List[string]]::new()
    foreach ($t in $truth) { if (-not $got.Contains($t)) { $problems.Add("missing $t") } }
    foreach ($x in $got) {
        if ($truth.Contains($x)) { continue }
        if (Test-Declaration $x $name) { $declExtras++ }
        else { $problems.Add("extra $x") }
    }
    foreach ($d in $defs) { if (-not $g.Defs.Contains($d) -and -not $g.PureDefs.Contains($d)) { $problems.Add("def $d is not a truth def") } }
    foreach ($d in $g.Defs) { if (-not $defs.Contains($d)) { $problems.Add("def $d not reported") } }
    foreach ($d in $g.PureDefs) { if ($defs.Contains($d)) { $problems.Add("pure virtual $d reported as a def (it has no body)") } else { $pureGaps++ } }

    if ($problems.Count -eq 0) { continue }
    $fail += $problems.Count
    Write-Host ("  FAIL {0} ({1}): {2}" -f $name, ($g.Keys -join ', '), ($problems -join '; ')) -ForegroundColor Red
}

Write-Host ("{0} simple names, {1} truth lines; {2} in-class declaration line(s) listed by name (expected), {3} pure virtual(s) with no body def (expected)" -f `
        $names, $truthLines, $declExtras, $pureGaps) -ForegroundColor Cyan
if ($fail -ne 0) { Write-Host "VERIFY-CPP: FAIL ($fail problem(s))" -ForegroundColor Red; exit 1 }
Write-Host "VERIFY-CPP: PASS" -ForegroundColor Green
