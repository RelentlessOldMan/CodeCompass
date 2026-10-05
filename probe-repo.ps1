<#
.SYNOPSIS
  Run CodeCompass against ANY repo, hit the areas of concern (memory / correctness / perf),
  and emit an ANONYMIZED, Discord-sized report plus a rich local JSON for analysis.

.WHY
  The load has to run where the big real repo lives (e.g. the 90 GB tree on the work box);
  the ANALYSIS is better done by the agent from a compact bundle. This decouples the two:
  the script produces raw observations, you paste the fenced report into Discord, the agent
  reads the curve. The report is data-rich (peak RSS + plateau-vs-climb per query), NOT a
  pass/fail verdict - the signal we need (174 climbed linearly; 175 is flat) only lives in
  the shape, so we keep it.

  Every query process is sampled for peak working set and KILLED if it crosses -CeilMB, so a
  runaway can never take the machine down (belt-and-suspenders on top of the in-app budget).

.EXAMPLES
  # Recommended: name the hot symbols you already know stress find_references.
  .\probe-repo.ps1 -Repo \\server\firmware -Symbols hot_helper,dma_start,reg_write

  # Or from a file (one symbol per line).
  .\probe-repo.ps1 -Repo D:\big -SymbolsFile hot.txt

  # Or let it auto-pick the broadest symbols (approximate; slower on huge trees).
  .\probe-repo.ps1 -Repo D:\big -AutoTop 12

  # Already indexed? Skip the (re)build.
  .\probe-repo.ps1 -Repo D:\big -Symbols foo,bar -SkipIndex
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Repo,
    [string]$Exe,                              # CodeCompass.Cli.exe; default: plugin\bin next to this script
    [string[]]$Symbols,                        # explicit symbols (comma or repeated)
    [string]$SymbolsFile,                      # one symbol per line
    [int]$AutoTop = 12,                        # if no symbols given, auto-pick this many broadest symbols
    [int]$AutoPool = 200,                      # candidate pool size to rank when auto-picking
    [int]$CeilMB = 0,                          # kill a query above this RSS (0 => 60% of physical RAM)
    [int]$IntervalMs = 200,                    # RSS sample cadence
    [switch]$SkipIndex,                        # don't (re)build the index first
    [switch]$KeepNames,                        # leave symbol names readable in the report (default: hashed)
    [string]$OutDir                            # where to write the JSON + report (default: script dir)
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
if (-not $Exe)    { $Exe = Join-Path $scriptDir 'plugin\bin\CodeCompass.Cli.exe' }
if (-not $OutDir) { $OutDir = $scriptDir }
if (-not (Test-Path $Exe))  { throw "CLI exe not found: $Exe  (build the plugin, or pass -Exe)" }
if (-not (Test-Path $Repo)) { throw "repo not found: $Repo" }
# .ProviderPath (not .Path): .Path returns the provider-qualified form for UNC paths
# (Microsoft.PowerShell.Core\FileSystem::\\server\share), which native process launches reject.
$Repo = (Resolve-Path $Repo).ProviderPath

if ($CeilMB -le 0) {
    $ramBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    $CeilMB = [int]([math]::Floor(($ramBytes / 1MB) * 0.60))
}
Write-Host "probe: repo=$Repo  ceil=${CeilMB}MB  interval=${IntervalMs}ms" -ForegroundColor Cyan

# --- helpers ---------------------------------------------------------------

function Get-Hash4([string]$s) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($s))
    -join ($bytes[0..1] | ForEach-Object { $_.ToString('x2') })   # 4 hex chars
}

# Run one CLI command as a child process, redirect its output to temp files (so a huge result
# set can't deadlock on a full pipe), sample WorkingSet64 every IntervalMs, and hard-kill it if
# it crosses the ceiling. Returns timing, peak/baseline RSS, tail slope, output, and a downsampled curve.
function Invoke-Sampled([string[]]$CliArgs) {
    # Quote each arg into a single command line (Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList).
    $cmdline = ($CliArgs | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = $cmdline
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $samples = New-Object System.Collections.Generic.List[object]
    $peakMB = 0.0; $baseMB = 0.0; $killed = $false
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$p.Start()
    # Drain both pipes asynchronously so a large result set can't deadlock the child on a full buffer.
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    try {
        while (-not $p.HasExited) {
            try { $p.Refresh(); $wsMB = $p.WorkingSet64 / 1MB } catch { break }
            if ($baseMB -eq 0.0) { $baseMB = $wsMB }
            if ($wsMB -gt $peakMB) { $peakMB = $wsMB }
            $samples.Add([pscustomobject]@{ t = $sw.Elapsed.TotalSeconds; mb = [math]::Round($wsMB, 1) })
            if ($wsMB -gt $CeilMB) {
                $killed = $true
                try { $p.Kill() } catch {}
                break
            }
            Start-Sleep -Milliseconds $IntervalMs
        }
        $p.WaitForExit()
    } finally { $sw.Stop() }

    # Tail slope over the last third of the run (MB/s): the plateau-vs-climb signal.
    $tailSlope = 0.0
    if ($samples.Count -ge 4) {
        $tCut = $samples[-1].t * 0.67
        $tail = @($samples | Where-Object { $_.t -ge $tCut })
        if ($tail.Count -ge 2) {
            $n = $tail.Count
            $sx = ($tail | Measure-Object -Property t -Sum).Sum
            $sy = ($tail | Measure-Object -Property mb -Sum).Sum
            $sxx = ($tail | ForEach-Object { $_.t * $_.t } | Measure-Object -Sum).Sum
            $sxy = ($tail | ForEach-Object { $_.t * $_.mb } | Measure-Object -Sum).Sum
            $denom = ($n * $sxx - $sx * $sx)
            if ([math]::Abs($denom) -gt 1e-9) { $tailSlope = [math]::Round((($n * $sxy - $sx * $sy) / $denom), 1) }
        }
    }

    $stdout = try { $outTask.Result } catch { '' }
    $stderr = try { $errTask.Result } catch { '' }
    if ($null -eq $stdout) { $stdout = '' }
    if ($null -eq $stderr) { $stderr = '' }

    # Downsample the curve to <=24 points for the JSON (report only ever needs a climb curve).
    # NOTE: use .ToArray(), not @($samples) - wrapping a generic List in @() throws under -EA Stop in PS 5.1.
    $curve = $samples.ToArray()
    if ($curve.Count -gt 24) {
        $step = [int][math]::Ceiling($curve.Count / 24.0)
        $curve = @(for ($i = 0; $i -lt $curve.Count; $i += $step) { $curve[$i] })
    }

    [pscustomobject]@{
        ExitCode = $p.ExitCode; Ms = [int]$sw.Elapsed.TotalMilliseconds
        PeakMB = [math]::Round($peakMB, 1); BaseMB = [math]::Round($baseMB, 1)
        TailSlopeMBs = $tailSlope; Killed = $killed
        Stdout = $stdout; Stderr = $stderr; Curve = $curve
    }
}

function Count-Lines([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return 0 }
    @($text -split "`n" | Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith('--') }).Count
}

# Plateau-vs-climb verdict. Only meaningful once a query runs long enough that the .NET process
# JIT/load ramp (a few hundred MB in the first ~1-2 s) is a small fraction of the window - the real
# broad find_references runs 20-30 s on a big tree. Short queries get 'warm' (peak is still reported,
# but the SHAPE isn't judged) so a sub-second query's startup ramp never reads as a false CLIMB.
$ClimbMinMs = 3000
$ClimbSlopeMBs = 5
function Get-RefFlag($killed, $ms, $tail) {
    if ($killed) { return 'KILL' }
    if ($ms -lt $ClimbMinMs) { return 'warm' }
    if ([math]::Abs($tail) -ge $ClimbSlopeMBs) { return 'CLMB' }
    return 'flat'
}

# --- resolve the symbol set ------------------------------------------------

$symbolSet = @()
if ($SymbolsFile) { $symbolSet += @(Get-Content $SymbolsFile | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
if ($Symbols)     { $symbolSet += @($Symbols | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$symbolSet = @($symbolSet | Select-Object -Unique)

if ($symbolSet.Count -eq 0) {
    Write-Host "no -Symbols given; auto-deriving the $AutoTop broadest symbols (approximate)..." -ForegroundColor Yellow
    $names = New-Object System.Collections.Generic.HashSet[string]
    foreach ($c in @([char[]]'abcdefghijklmnopqrstuvwxyz_0123456789')) {
        if ($names.Count -ge $AutoPool) { break }
        $r = Invoke-Sampled @('symbols', $Repo, "$c")
        foreach ($ln in ($r.Stdout -split "`n")) {
            if ($ln -match ':\d+:\d+:\s+\S+\s+(\S+)\s*$') {
                [void]$names.Add($Matches[1])
                if ($names.Count -ge $AutoPool) { break }
            }
        }
    }
    # Rank the pool by literal-search breadth (MORE EXIST = very broad => rank first).
    $ranked = foreach ($nm in $names) {
        $s = Invoke-Sampled @('search', $Repo, $nm)
        $more = $s.Stderr -match 'MORE EXIST'
        $cnt = 0
        if ($s.Stderr -match '(\d+)\s+match') { $cnt = [int]$Matches[1] }
        [pscustomobject]@{ Name = $nm; Breadth = $(if ($more) { 100000 + $cnt } else { $cnt }) }
    }
    $symbolSet = @($ranked | Sort-Object Breadth -Descending | Select-Object -First $AutoTop -ExpandProperty Name)
    Write-Host ("auto-picked: " + ($symbolSet -join ', ')) -ForegroundColor Yellow
}
if ($symbolSet.Count -eq 0) { throw "no symbols to probe (auto-derive found none; pass -Symbols)" }

# --- index (timed + sampled) ----------------------------------------------

$indexInfo = $null
if (-not $SkipIndex) {
    Write-Host "indexing (sampled)..." -ForegroundColor Cyan
    $ix = Invoke-Sampled @('index', $Repo)
    $indexInfo = [pscustomobject]@{
        Ms = $ix.Ms; PeakMB = $ix.PeakMB; TailSlopeMBs = $ix.TailSlopeMBs
        Killed = $ix.Killed; ExitCode = $ix.ExitCode
    }
    if ($ix.Killed) { Write-Warning "index run hit the ${CeilMB}MB ceiling and was killed." }
}

# --- doctor (numbers only; no path lines echoed) ---------------------------

$doc = Invoke-Sampled @('doctor', $Repo)
function Grab([string]$pattern) { foreach ($ln in ($doc.Stdout -split "`n")) { if ($ln -match $pattern) { return $ln.Trim() } } return $null }
$sidecarLine = Grab 'sidecar cache:'
$filesLine   = Grab 'files \(at build\):'   # doctor's line is "files (at build): 43,422" - no colon after "files"

# --- query battery ---------------------------------------------------------

Write-Host "running battery over $($symbolSet.Count) symbol(s)..." -ForegroundColor Cyan
$rows = foreach ($sym in $symbolSet) {
    $id = if ($KeepNames) { $sym } else { Get-Hash4 $sym }

    $refs = Invoke-Sampled @('refs', $Repo, $sym)
    # refs stderr: "-- {cs} C# semantic + {named} name-matched reference(s)" (C/C++ is a name search: the
    # empty middle group keeps RefCpp = 0 and RefLex = the name matches, so the report columns stay stable)
    $cs = 0; $cpp = 0; $lex = 0
    if ($refs.Stderr -match '(\d+)\s+C# semantic\s+\+\s+()(\d+)\s+name-matched') {
        $cs = [int]$Matches[1]; $cpp = [int]$Matches[2]; $lex = [int]$Matches[3]
    }
    $refTotal = $cs + $cpp + $lex
    # coverage disclosure (stdout): "X/Y candidate C/C++ file(s) parsed" => incomplete pass
    $parsed = $null; $cand = $null; $incomplete = $false
    if ($refs.Stdout -match '(\d+)/(\d+)\s+candidate C/C\+\+ file\(s\) parsed') {
        $parsed = [int]$Matches[1]; $cand = [int]$Matches[2]; $incomplete = ($parsed -lt $cand)
    }

    # determinism: a second refs run should return the same total.
    $refs2 = Invoke-Sampled @('refs', $Repo, $sym)
    $cs2 = 0; $cpp2 = 0; $lex2 = 0
    if ($refs2.Stderr -match '(\d+)\s+C# semantic\s+\+\s+()(\d+)\s+name-matched') {
        $cs2 = [int]$Matches[1]; $cpp2 = [int]$Matches[2]; $lex2 = [int]$Matches[3]
    }
    $deterministic = (($cs2 + $cpp2 + $lex2) -eq $refTotal)

    $srch = Invoke-Sampled @('search', $Repo, $sym)
    $defs = Invoke-Sampled @('def', $Repo, $sym)
    $syms = Invoke-Sampled @('symbols', $Repo, $sym)

    [pscustomobject]@{
        Id = $id; Sym = $sym
        RefCs = $cs; RefCpp = $cpp; RefLex = $lex; RefTotal = $refTotal
        Parsed = $parsed; Candidates = $cand; Incomplete = $incomplete
        RefMs = $refs.Ms; RefPeakMB = $refs.PeakMB; RefBaseMB = $refs.BaseMB
        RefTailMBs = $refs.TailSlopeMBs; RefKilled = $refs.Killed
        RefCurve = $refs.Curve
        Deterministic = $deterministic
        SearchMs = $srch.Ms; DefMs = $defs.Ms; SymMs = $syms.Ms
    }
    $flag = Get-RefFlag $refs.Killed $refs.Ms $refs.TailSlopeMBs
    Write-Host ("  {0}  refs={1,-5} {2,6}ms  peak={3,7:N0}MB  {4}" -f $id, $refTotal, $refs.Ms, $refs.PeakMB, $flag)
}

# --- assemble the anonymized, Discord-sized report -------------------------

$version = (& $Exe version) 2>$null
$maxPeak = ($rows | Measure-Object -Property RefPeakMB -Maximum).Maximum
$maxPeakRow = $rows | Sort-Object RefPeakMB -Descending | Select-Object -First 1
$anyClimb = @($rows | Where-Object { (Get-RefFlag $_.RefKilled $_.RefMs $_.RefTailMBs) -in @('CLMB', 'KILL') }).Count
$detOk = @($rows | Where-Object { $_.Deterministic }).Count
$repoHash = Get-Hash4 $Repo

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('```')
[void]$sb.AppendLine("CodeCompass probe - $version   repo=$repoHash(hashed)")
if ($filesLine)   { [void]$sb.AppendLine($filesLine) }
if ($indexInfo)   { [void]$sb.AppendLine("index: $($indexInfo.Ms)ms - peak RSS $([math]::Round($indexInfo.PeakMB))MB$(if($indexInfo.Killed){' - KILLED'})") }
if ($sidecarLine) { [void]$sb.AppendLine($sidecarLine) }
[void]$sb.AppendLine("ceiling: ${CeilMB}MB (kill if exceeded)")
[void]$sb.AppendLine('')
[void]$sb.AppendLine("battery ($($symbolSet.Count) symbols)")
[void]$sb.AppendLine(" id   refs   ms    peakMB tail  cov")
foreach ($r in $rows) {
    $flag = Get-RefFlag $r.RefKilled $r.RefMs $r.RefTailMBs
    $cov = if ($null -ne $r.Candidates) { "$($r.Parsed)/$($r.Candidates)" } else { '-' }
    [void]$sb.AppendLine(("{0} {1,5} {2,6} {3,7:N0} {4,-4} {5}" -f $r.Id, $r.RefTotal, $r.RefMs, $r.RefPeakMB, $flag, $cov))
    if ($flag -in @('CLMB', 'KILL')) {
        $pts = ($r.RefCurve | ForEach-Object { $_.mb }) -join ' '
        [void]$sb.AppendLine((" !{0} tail={1}MB/s curve(MB): {2}" -f $r.Id, $r.RefTailMBs, $pts))
    }
}
$peakVerdict = if ($anyClimb -eq 0) { "no query above the kill ceiling OK" } else { "$anyClimb query(s) grew to their per-query budget (see curves)" }
[void]$sb.AppendLine("peaks: max $([math]::Round($maxPeak))MB ($($maxPeakRow.Id)) - $peakVerdict")
# Honesty: this probe runs each query in a FRESH CLI process (base RSS ~7 MB), so a bounded query that grows
# to its budget then exits reads as CLMB by tail-slope - that is EXPECTED, not a runaway. A cross-query
# ratchet (the dangerous shape) only shows in a long-lived server; use the MCP-session mode for that.
[void]$sb.AppendLine("note: CLI-per-process mode - CLMB = one query using its budget, NOT a cross-query climb (use MCP-session mode to catch that)")
[void]$sb.AppendLine("determinism: $detOk/$($rows.Count) repeats identical")
[void]$sb.AppendLine('```')
$report = $sb.ToString()

# --- write outputs ---------------------------------------------------------

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath   = Join-Path $OutDir "probe-$repoHash-$stamp.json"
$reportPath = Join-Path $OutDir "probe-$repoHash-$stamp.txt"
$bundle = [pscustomobject]@{
    version = $version; repoHash = $repoHash; keptNames = [bool]$KeepNames
    ceilMB = $CeilMB; intervalMs = $IntervalMs
    index = $indexInfo; sidecar = $sidecarLine; files = $filesLine
    rows = $rows
}
$bundle | ConvertTo-Json -Depth 8 | Out-File -FilePath $jsonPath -Encoding utf8
$report | Out-File -FilePath $reportPath -Encoding utf8

Write-Host ""
Write-Host "=== paste this into Discord ===" -ForegroundColor Green
Write-Host $report
Write-Host "report: $reportPath" -ForegroundColor DarkGray
Write-Host "json:   $jsonPath (rich, local, non-anonymized$(if(-not $KeepNames){' names hashed in report only'}))" -ForegroundColor DarkGray
Write-Host ("report chars: {0} (Discord msg limit 2000)" -f $report.Length) -ForegroundColor DarkGray
