<#
.SYNOPSIS
  Run `codecompass selftest` on several repos and put the results side by side: one table of index time and memory,
  pass/fail and per-tool timings - the numbers behind the docs' "Self-test across repos" table, on any machine.

.DESCRIPTION
  For each repo it runs the full self-test (a fresh index, every MCP tool over stdio with each answer checked against
  the files, the deep find_references sweep), keeping each repo's HTML page and JSON. Then it writes:

    matrix.md    a Markdown table, paste-ready
    matrix.html  the same table as a self-contained page, each repo linked to its own report
    matrix.json  one row per repo, for other scripts

  Per-tool times are the warm MCP calls the self-test made (median / slowest). The deep sweep is the repo's broadest
  names through find_references, up to 1,000 references each: its slowest call and the server's peak memory.
  Nothing is written into the repos; each one's index cache is rebuilt, as the self-test always does.

  Allow a few minutes per ordinary repo and 10-30+ minutes for a huge or network one (the index and the deep sweep
  dominate). Run it on a quiet machine if the times are going to be published.

.PARAMETER Repos
  Repo folders (local or UNC) to test.

.PARAMETER Corpora
  Pinned corpus ids (see bench\corpus-manifest.json), fetched via fetch-corpus.ps1 if not present.
  Default (no -Repos/-Corpora): the README preset - requests, fmt, efcore, typescript, godot, roslyn, llvm.

.PARAMETER Exe
  CodeCompass.Cli.exe to use (its CodeCompass.Mcp.exe must sit next to it). Default: plugin\bin next to this repo.

.PARAMETER Anonymize
  Pass --anonymize to each self-test: names and paths hashed, answers left out - for a table you can share from a
  private repo. The repo column then shows each repo's hashed label.

.PARAMETER OutDir
  Where to write. Default: %LOCALAPPDATA%\CodeCompass\selftest\matrix-<yyyyMMdd-HHmmss>.

.PARAMETER Resume
  Keep the result of each repo already tested in -OutDir and only test the rest; with every repo done, this just
  rebuilds the table. Lets each repo run on its own (e.g. -Corpora llvm -OutDir X -Resume, one at a time).

.EXAMPLE
  .\scripts\selftest-matrix.ps1
  .\scripts\selftest-matrix.ps1 -Corpora requests,fmt
  .\scripts\selftest-matrix.ps1 -Repos D:\work\big-repo -Anonymize
#>
[CmdletBinding()]
param(
    [string[]]$Repos,
    [string[]]$Corpora,
    [string]$Exe,
    [switch]$Anonymize,
    [string]$OutDir,
    [switch]$Resume
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'plugin\bin\CodeCompass.Cli.exe' }
if (-not (Test-Path $Exe)) { throw "CLI not found: $Exe (build the plugin, or pass -Exe)" }
if (-not $OutDir) { $OutDir = Join-Path $env:LOCALAPPDATA ("CodeCompass\selftest\matrix-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force $OutDir | Out-Null

# ---- the work list: explicit paths, named corpora, or the README preset (as bench-matrix.ps1 resolves them) ----
$work = New-Object System.Collections.Generic.List[object]
foreach ($p in @($Repos | Where-Object { $_ })) {
    if (-not (Test-Path $p)) { Write-Warning "repo not found: $p"; continue }
    $rp = (Resolve-Path $p).ProviderPath
    $work.Add([pscustomobject]@{ Id = (Split-Path $rp -Leaf); Path = $rp })
}
$ids = if ($Corpora) { @($Corpora | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }
       elseif (-not $Repos) { 'requests','fmt','efcore','typescript','godot','roslyn','llvm' } else { @() }
foreach ($id in $ids) {
    $base = Join-Path $root ".corpus\$id"
    if (-not (Test-Path $base)) {
        Write-Host "[$id] fetching..." -ForegroundColor DarkCyan
        try { & (Join-Path $root 'fetch-corpus.ps1') $id } catch { Write-Warning "fetch $id failed: $($_.Exception.Message)"; continue }
    }
    $rp = (Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    if (-not $rp) { Write-Warning "no extracted root for $id"; continue }
    $work.Add([pscustomobject]@{ Id = $id; Path = $rp })
}
if ($work.Count -eq 0) { throw "nothing to test (pass -Repos, -Corpora, or neither for the README preset)" }

function Median([double[]]$xs) {
    if (-not $xs -or $xs.Count -eq 0) { return $null }
    $s = @($xs | Sort-Object); $n = $s.Count
    if ($n % 2) { return $s[($n - 1) / 2] } else { return ($s[$n / 2 - 1] + $s[$n / 2]) / 2 }
}
function Fact($j, $label) { ($j.facts | Where-Object { $_.label -eq $label } | Select-Object -First 1).value }
function Ms($v) { if ($null -eq $v) { '-' } elseif ($v -ge 10000) { '{0:N1} s' -f ($v / 1000) } else { '{0:N0} ms' -f $v } }
function Dur($s) { if ($s -ge 60) { '{0}m {1:00}s' -f [int][math]::Floor($s / 60), [int]($s % 60) } else { '{0:N1} s' -f $s } }
function Mb($v) { if ($null -eq $v -or $v -lt 0) { '-' } elseif ($v -ge 1024) { '{0:N1} GB' -f ($v / 1024) } else { '{0:N0} MB' -f $v } }

$tools = 'search_code', 'find_definition', 'search_symbols', 'find_references', 'find_callees'
$rows = New-Object System.Collections.Generic.List[object]
$i = 0
foreach ($w in $work) {
    $i++
    $safe = ($w.Id -replace '[^\w.-]', '_')
    $json = Join-Path $OutDir "$safe.json"; $html = Join-Path $OutDir "$safe.html"
    Write-Host ("[{0}/{1}] {2}  ({3})" -f $i, $work.Count, $w.Id, $w.Path) -ForegroundColor Cyan
    $stArgs = @('selftest', $w.Path, '--json', $json, '--html', $html)
    if ($Anonymize) { $stArgs += '--anonymize' }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $exitFile = Join-Path $OutDir "$safe.exit"
    if ($Resume -and (Test-Path $json) -and (Test-Path $exitFile)) {
        $exit = [int](Get-Content $exitFile -Raw)
        Write-Host "  kept the earlier result"
    } else {
        $ErrorActionPreference = 'Continue'   # the self-test's progress goes to stderr; PowerShell 5.1 would stop on it
        & $Exe @stArgs 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath (Join-Path $OutDir "$safe.log") | Out-Host
        $exit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        Set-Content $exitFile $exit
    }
    if (-not (Test-Path $json)) { Write-Warning "$($w.Id): no JSON written (exit $exit)"; continue }
    $j = Get-Content $json -Raw -Encoding UTF8 | ConvertFrom-Json

    $index = $j.steps | Where-Object { $_.feature -eq 'Indexing' -and $_.ms -ge 0 } | Select-Object -First 1
    $per = [ordered]@{}
    foreach ($t in $tools) {
        $ms = @($j.steps | Where-Object { $_.feature -eq $t -and $_.ms -ge 0 } | ForEach-Object { [double]$_.ms })
        $per[$t] = if ($ms.Count) { [pscustomobject]@{ median = (Median $ms); max = ($ms | Measure-Object -Maximum).Maximum; calls = $ms.Count } } else { $null }
    }
    $sweep = @($j.sweep)
    $rows.Add([pscustomobject]@{
        id = if ($Anonymize) { $j.repo } else { $w.Id }
        report = Split-Path $html -Leaf
        verdict = $j.verdict; passed = $j.passed; warned = $j.warned; failed = $j.failed; exit = $exit
        location = Fact $j 'Location'
        files = Fact $j 'Files indexed'
        text = Fact $j 'Text indexed'
        symbols = Fact $j 'Symbols'
        indexOnDisk = Fact $j 'Index on disk'
        indexMs = if ($index) { $index.ms } else { $null }
        indexPeakMb = if ($index) { $index.peakMb } else { $null }
        serverPeakMb = $j.server.peakMb
        tools = $per
        sweepMaxMs = if ($sweep.Count) { ($sweep | Measure-Object ms -Maximum).Maximum } else { $null }
        sweepMaxRefs = if ($sweep.Count) { ($sweep | Measure-Object shown -Maximum).Maximum } else { $null }
        sweepDeterministic = if ($sweep.Count) { -not ($sweep | Where-Object { -not $_.deterministic }) } else { $null }
        seconds = $j.seconds
        version = $j.version
        machine = Fact $j 'Machine'
    })
    Write-Host ("  {0}: {1} in {2}" -f $w.Id, $j.verdict, (Dur $sw.Elapsed.TotalSeconds))
}
if ($rows.Count -eq 0) { throw "no self-test produced a result" }

# ---- the table ----
$head = @('Repo', 'Result', 'Files', 'Index', 'Index peak', 'Server peak') + ($tools | ForEach-Object { "$_ med / max" }) + @('Deep refs max', 'Total')
function Cells($r) {
    $c = @($r.id, ("{0} ({1}/{2}/{3})" -f $r.verdict, $r.passed, $r.warned, $r.failed), $r.files, (Dur ($r.indexMs / 1000)), (Mb $r.indexPeakMb), (Mb $r.serverPeakMb))
    foreach ($t in $tools) { $v = $r.tools[$t]; $c += if ($v) { "{0} / {1}" -f (Ms $v.median), (Ms $v.max) } else { 'n/a' } }
    $c += if ($null -ne $r.sweepMaxMs) { "{0} ({1} refs)" -f (Ms $r.sweepMaxMs), $r.sweepMaxRefs } else { '-' }
    $c += Dur $r.seconds
    , $c
}
$versions = ($rows.version | Select-Object -Unique) -join ', '
$machine = ($rows.machine | Select-Object -First 1)
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("CodeCompass $versions self-test, $(Get-Date -Format 'yyyy-MM-dd'), on $machine.")
[void]$md.AppendLine("Result = passed/warnings/failed checks. Tool times are the self-test's warm MCP calls; deep refs = the broadest names, up to 1,000 references each.")
[void]$md.AppendLine()
[void]$md.AppendLine('| ' + ($head -join ' | ') + ' |')
[void]$md.AppendLine('|' + (($head | ForEach-Object { '---' }) -join '|') + '|')
foreach ($r in $rows) { [void]$md.AppendLine('| ' + ((Cells $r) -join ' | ') + ' |') }
$md.ToString() | Set-Content (Join-Path $OutDir 'matrix.md') -Encoding UTF8
$rows | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir 'matrix.json') -Encoding UTF8

$enc = { param($s) [System.Net.WebUtility]::HtmlEncode([string]$s) }
$h = New-Object System.Text.StringBuilder
[void]$h.Append(@"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Self-test matrix</title><style>
:root{--bg:#f6f8fa;--card:#fff;--ink:#1f2328;--muted:#59636e;--line:#d1d9e0;--head:#eef1f4;--good:#1a7f37;--bad:#cf222e;--warn:#9a6700;--accent:#0969da}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#0f1720;--card:#1b2430;--ink:#e6edf3;--muted:#9fb0c0;--line:#2b3947;--head:#141d28;--good:#3fb950;--bad:#f0645b;--warn:#d29922;--accent:#4c8bf5}}
:root[data-theme="dark"]{--bg:#0f1720;--card:#1b2430;--ink:#e6edf3;--muted:#9fb0c0;--line:#2b3947;--head:#141d28;--good:#3fb950;--bad:#f0645b;--warn:#d29922;--accent:#4c8bf5}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;padding:28px 16px}
.wrap{max-width:1300px;margin:0 auto} h1{font-size:22px;margin:0 0 4px} .sub{color:var(--muted);margin:0 0 18px}
.scroll{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--card)}
table{width:100%;border-collapse:collapse} th,td{padding:8px 10px;text-align:left;border-bottom:1px solid var(--line);white-space:nowrap}
th{background:var(--head);color:var(--muted);font-weight:600;font-size:12px} td.num{text-align:right;font-variant-numeric:tabular-nums}
a{color:var(--accent)} .PASS{color:var(--good);font-weight:600} .FAIL{color:var(--bad);font-weight:600} .warn{color:var(--warn);font-weight:600}
</style></head><body><div class="wrap"><h1>Self-test across repos</h1>
"@)
[void]$h.Append("<p class=`"sub`">CodeCompass $(& $enc $versions), $(Get-Date -Format 'yyyy-MM-dd'), on $(& $enc $machine). Result = passed/warnings/failed checks. Tool times are the self-test's warm MCP calls (median / slowest); deep refs = the broadest names, up to 1,000 references each.</p>")
[void]$h.Append('<div class="scroll"><table><thead><tr>' + (($head | ForEach-Object { "<th>$(& $enc $_)</th>" }) -join '') + '</tr></thead><tbody>')
foreach ($r in $rows) {
    $c = Cells $r
    $cls = if ($r.failed -gt 0) { 'FAIL' } elseif ($r.warned -gt 0) { 'warn' } else { 'PASS' }
    [void]$h.Append("<tr><td><a href=`"$(& $enc $r.report)`">$(& $enc $c[0])</a></td><td class=`"$cls`">$(& $enc $c[1])</td>")
    for ($k = 2; $k -lt $c.Count; $k++) { [void]$h.Append("<td class=`"num`">$(& $enc $c[$k])</td>") }
    [void]$h.Append('</tr>')
}
[void]$h.Append('</tbody></table></div></div></body></html>')
$h.ToString() | Set-Content (Join-Path $OutDir 'matrix.html') -Encoding UTF8

Write-Host ""
Write-Host $md.ToString()
Write-Host "written: $OutDir (matrix.md, matrix.html, matrix.json, one .html/.json/.log per repo)"
if ($rows | Where-Object { $_.failed -gt 0 -or $_.exit -ne 0 }) { exit 2 }
