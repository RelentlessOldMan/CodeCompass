<#
.SYNOPSIS
  Build a per-tool x per-repo performance matrix for CodeCompass - the numbers behind the
  README/docs "Performance" table - reproducibly, on any machine, against any corpus.

.DESCRIPTION
  For each repo it (optionally) builds a fresh index (timed = the "Build" column), then times every
  tool (search_code / find_definition / search_symbols / find_references) TWO ways:

    COLD - one fresh CLI process per query (via probe-repo.ps1). This is what you pay the FIRST time
           a tool is used after launch: process start + mmap-open + (for find_references) cold analyzer
           build. It's the honest worst case, and the only number a one-shot CLI user ever sees.

    WARM - the same queries driven over stdio against ONE long-lived MCP server (the real path an
           agent uses via Claude Code / Codex). Steady-state latency once the index is mapped and the
           semantic analyzer is resident - typically ~1 ms for the index-backed tools.

  Both are measured on the box you run it on, so a third party reproduces our table by running the exact
  same command - either against the pinned public corpora we benchmark (fetched on demand via
  fetch-corpus.ps1) or against their own tree. Output is a console table, a Markdown table (paste-ready
  for the docs) with cold/warm per cell, and a JSON bundle. Use -NoWarm for cold only.

  find_references is the only parse-based tool, so its latency depends on the symbol; the battery
  auto-picks the BROADEST symbols (its stress case) and reports the median plus the max, consistent
  with how the big-corpus row is measured. For warm find_references it also reports the one-time
  analyzer-build cost paid on the first semantic call of the session, separate from the warm median.

.PARAMETER Corpora
  Manifest corpus ids to measure (e.g. requests,llvm). Fetched via fetch-corpus.ps1 if not present.
  Default (no -Repos/-Corpora): the README preset - requests, fmt, efcore, typescript, godot, roslyn, llvm.

.PARAMETER Repos
  Explicit repo paths to measure (any tree, local or UNC). Use this for your own code / a network share.

.PARAMETER Exe
  CodeCompass.Cli.exe to use. Default: plugin\bin\CodeCompass.Cli.exe next to this script.

.PARAMETER McpExe
  CodeCompass.Mcp.exe to use for the WARM battery. Default: CodeCompass.Mcp.exe next to -Exe.

.PARAMETER NoWarm
  Skip the warm (MCP-session) battery; report cold CLI numbers only.

.PARAMETER SkipIndexTiming
  Don't (re)build the index; measure queries against whatever index already exists (Build shows "cached").
  Use this on a huge/UNC tree you've already indexed and don't want to rebuild.

.PARAMETER AutoTop / .PARAMETER AutoPool / .PARAMETER Symbols
  Passed through to probe-repo.ps1 for symbol selection (how many broadest symbols, candidate pool, or
  an explicit symbol list).

.PARAMETER OutDir
  Where to write matrix-*.json, matrix-summary.json and perf-matrix.md. Default: %TEMP%\cc-matrix.

.EXAMPLE
  # Reproduce the README table (fetches the pinned corpora as needed):
  pwsh ./bench-matrix.ps1

.EXAMPLE
  # Measure your own repo (and a network share):
  pwsh ./bench-matrix.ps1 -Repos "D:\work\myrepo","\\nas\team\bigrepo"

.EXAMPLE
  # Just two of the pinned corpora:
  pwsh ./bench-matrix.ps1 -Corpora requests,llvm
#>
[CmdletBinding()]
param(
    [string[]]$Corpora,
    [string[]]$Repos,
    [string]$Exe,
    [string]$McpExe,
    [switch]$NoWarm,
    [switch]$SkipIndexTiming,
    [int]$AutoTop = 6,
    [int]$AutoPool = 60,
    [string[]]$Symbols,
    [int]$WarmIters = 5,
    [string]$OutDir
)

$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
if (-not $Exe)    { $Exe = Join-Path $root 'plugin\bin\CodeCompass.Cli.exe' }
if (-not $McpExe) { $McpExe = Join-Path (Split-Path $Exe) 'CodeCompass.Mcp.exe' }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP 'cc-matrix' }
if (-not (Test-Path $Exe)) { throw "CLI exe not found: $Exe  (build the plugin, or pass -Exe)" }
if (-not $NoWarm -and -not (Test-Path $McpExe)) {
    Write-Warning "MCP exe not found: $McpExe - warm battery disabled (pass -McpExe or -NoWarm to silence)."
    $NoWarm = $true
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# --- warm (MCP-session) battery ------------------------------------------------
# Drive ONE long-lived MCP server over stdio - the exact path an agent uses - and time each tool at
# steady state (index mapped, semantic analyzer resident). Returns per-tool medians plus the one-time
# analyzer-build cost paid on the first find_references of the session. $null if the session fails.
function Invoke-WarmBattery([string]$RepoPath, [string]$ServerExe, [string[]]$SymbolSet, [int]$Iters) {
    $searchTokens = @('return', 'class', 'public', 'const', 'value', 'string', 'for ', 'if (')
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ServerExe
    $psi.WorkingDirectory = $RepoPath            # the server serves its working directory
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi
    try { [void]$proc.Start() } catch { Write-Warning "warm: failed to launch $ServerExe : $($_.Exception.Message)"; return $null }
    $errTask = $proc.StandardError.ReadToEndAsync()   # drain server logs so they can't fill the pipe

    $script:rpcId = 1
    # Write one JSON-RPC line and read stdout until the matching response (or timeout). Returns the text
    # payload of a tools/call result, '' on timeout/EOF.
    function Send-Call([string]$tool, [string]$argJson, [int]$timeoutSec) {
        $id = ++$script:rpcId
        $req = '{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":{"name":"' + $tool + '","arguments":' + $argJson + '}}'
        $proc.StandardInput.WriteLine($req); $proc.StandardInput.Flush()
        $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSec)
        while ($true) {
            $rt = $proc.StandardOutput.ReadLineAsync()
            while (-not $rt.Wait(200)) { if ([DateTime]::UtcNow -gt $deadline -or $proc.HasExited) { return '' } }
            $line = $rt.Result
            if ($null -eq $line) { return '' }
            $obj = $null; try { $obj = $line | ConvertFrom-Json } catch { continue }
            if (($obj.PSObject.Properties.Name -contains 'id') -and $obj.id -eq $id) {
                if ($null -eq $obj.result) { return '' }
                $c = $obj.result.content
                if ($c -and $c.Count -ge 1) { return (($c | ForEach-Object { $_.text }) -join "`n") }
                return ''
            }
        }
    }
    function Time-Call([string]$tool, [string]$argJson, [int]$timeoutSec) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $txt = Send-Call $tool $argJson $timeoutSec
        $sw.Stop()
        return [pscustomobject]@{ Ms = [int]$sw.Elapsed.TotalMilliseconds; Text = $txt }
    }
    function Esc([string]$s) { return ($s -replace '\\', '\\' -replace '"', '\"') }

    try {
        # Handshake.
        $hid = ++$script:rpcId
        $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":' + $hid + ',"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"bench-matrix","version":"1"}}}')
        $proc.StandardInput.Flush()
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ($true) {
            $rt = $proc.StandardOutput.ReadLineAsync()
            while (-not $rt.Wait(200)) { if ([DateTime]::UtcNow -gt $deadline -or $proc.HasExited) { Write-Warning 'warm: handshake timed out'; return $null } }
            $line = $rt.Result; if ($null -eq $line) { return $null }
            $obj = $null; try { $obj = $line | ConvertFrom-Json } catch { continue }
            if (($obj.PSObject.Properties.Name -contains 'id') -and $obj.id -eq $hid) { break }
        }
        $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}'); $proc.StandardInput.Flush()

        # Wait for the index to be ready (we pre-indexed, so the server TryLoads it on the first query; a
        # huge/UNC tree can take a moment to map). Poll a cheap search until it stops returning a status msg.
        $ready = $false
        for ($i = 0; $i -lt 60; $i++) {
            $r = Send-Call 'search_code' '{"query":"return","maxResults":1}' 60
            if ($r -and $r -notmatch 'indexing|hasn''t indexed|Build the index|codecompass index') { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { Write-Warning "warm: index not ready for $RepoPath (server still building?) - skipping warm"; return $null }

        # search_code: generic hot tokens, several iterations each -> median across all.
        $searchMs = New-Object System.Collections.Generic.List[int]
        foreach ($tok in $searchTokens) { for ($k = 0; $k -lt $Iters; $k++) { $searchMs.Add((Time-Call 'search_code' ('{"query":"' + (Esc $tok) + '","maxResults":50}') 60).Ms) } }

        # find_definition + search_symbols over the same auto-picked symbols as the cold battery.
        $defMs = New-Object System.Collections.Generic.List[int]
        $symMs = New-Object System.Collections.Generic.List[int]
        foreach ($s in $SymbolSet) {
            for ($k = 0; $k -lt $Iters; $k++) {
                $defMs.Add((Time-Call 'find_definition' ('{"name":"' + (Esc $s) + '"}') 60).Ms)
                $symMs.Add((Time-Call 'search_symbols' ('{"query":"' + (Esc $s) + '","maxResults":50}') 60).Ms)
            }
        }

        # find_references: the FIRST semantic call of the session builds the analyzer (cold); report that
        # one-time cost separately, then the warm per-symbol medians.
        $refColdMs = $null; $refWarmMs = New-Object System.Collections.Generic.List[int]
        $first = $true
        foreach ($s in $SymbolSet) {
            $r = Time-Call 'find_references' ('{"name":"' + (Esc $s) + '","maxResults":100}') 300
            if ($first) { $refColdMs = $r.Ms; $first = $false; $r2 = Time-Call 'find_references' ('{"name":"' + (Esc $s) + '","maxResults":100}') 300; $refWarmMs.Add($r2.Ms) }
            else { $refWarmMs.Add($r.Ms) }
        }

        function MedL($list) { $a = @($list | Sort-Object); if (-not $a.Count) { return $null }; return [int]$a[[int][math]::Floor(($a.Count - 1) / 2)] }
        function MaxL($list) { if (-not @($list).Count) { return $null }; return [int](($list | Measure-Object -Maximum).Maximum) }
        return [pscustomobject]@{
            searchMs    = MedL $searchMs
            defMs       = MedL $defMs
            symMs       = MedL $symMs
            refsMs      = MedL $refWarmMs
            refsMaxMs   = MaxL $refWarmMs
            refsColdMs  = $refColdMs
        }
    }
    finally {
        try { $proc.StandardInput.Close() } catch {}
        if (-not $proc.WaitForExit(5000)) { try { $proc.Kill() } catch {} }
    }
}

# Language labels for the pinned corpora (for the table; blank for arbitrary -Repos).
$manifestPath = Join-Path $root 'bench\corpus-manifest.json'
$langById = @{}
if (Test-Path $manifestPath) {
    foreach ($c in (Get-Content $manifestPath -Raw | ConvertFrom-Json).corpora) { $langById[$c.id] = $c.language }
}

# Resolve the work list: explicit paths, named corpora, or the README preset.
$README_PRESET = 'requests','fmt','efcore','typescript','godot','roslyn','llvm'
$work = New-Object System.Collections.Generic.List[object]   # {Id, Path, Lang}

if ($Repos) {
    foreach ($p in $Repos) {
        if (-not (Test-Path $p)) { Write-Warning "repo not found: $p"; continue }
        $rp = (Resolve-Path $p).ProviderPath          # .ProviderPath so UNC paths launch natively
        $work.Add([pscustomobject]@{ Id = (Split-Path $rp -Leaf); Path = $rp; Lang = '' })
    }
}
$ids = if ($Corpora) { $Corpora } elseif (-not $Repos) { $README_PRESET } else { @() }
foreach ($id in $ids) {
    $base = Join-Path $root ".corpus\$id"
    if (-not (Test-Path $base)) {
        Write-Host "[$id] fetching..." -ForegroundColor DarkCyan
        try { & "$root\fetch-corpus.ps1" $id } catch { Write-Warning "fetch $id failed: $($_.Exception.Message)"; continue }
    }
    $rp = (Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    if (-not $rp) { Write-Warning "no extracted root for $id"; continue }
    $work.Add([pscustomobject]@{ Id = $id; Path = $rp; Lang = $langById[$id] })
}
if ($work.Count -eq 0) { throw "nothing to measure (pass -Repos, -Corpora, or neither for the README preset)" }

# Measure each repo: time a fresh index (the Build column), then run the per-tool battery.
$indexMsById = @{}
$warmById = @{}
foreach ($w in $work) {
    if ($SkipIndexTiming) {
        $indexMsById[$w.Id] = $null
    } else {
        Write-Host "=== INDEX $($w.Id) -> $($w.Path) ===" -ForegroundColor Cyan
        try {
            $t = Measure-Command { & $Exe index $w.Path *> $null }
            $indexMsById[$w.Id] = [int]$t.TotalMilliseconds
            Write-Host ("  indexed in {0} ms" -f $indexMsById[$w.Id])
        } catch { Write-Warning "index $($w.Id) failed: $($_.Exception.Message)"; continue }
    }

    Write-Host "=== PROBE $($w.Id) ===" -ForegroundColor Cyan
    $before = @(Get-ChildItem $OutDir -Filter 'probe-*.json' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $probeArgs = @{ Repo = $w.Path; Exe = $Exe; SkipIndex = $true; KeepNames = $true; AutoTop = $AutoTop; AutoPool = $AutoPool; OutDir = $OutDir }
    if ($Symbols) { $probeArgs.Symbols = $Symbols }
    try { & "$root\probe-repo.ps1" @probeArgs } catch { Write-Warning "probe $($w.Id) failed: $($_.Exception.Message)" }
    $new = Get-ChildItem $OutDir -Filter 'probe-*.json' -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -notin $before } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($new) { Copy-Item $new.FullName (Join-Path $OutDir "matrix-$($w.Id).json") -Force }

    # WARM pass: reuse the SAME symbols the cold probe just auto-picked (from its JSON) so cold and warm
    # time identical queries, then drive them over a single long-lived MCP server.
    if (-not $NoWarm) {
        $symSet = @()
        $mf = Join-Path $OutDir "matrix-$($w.Id).json"
        if (Test-Path $mf) { $symSet = @((Get-Content $mf -Raw | ConvertFrom-Json).rows | ForEach-Object { $_.Sym } | Where-Object { $_ }) }
        if ($Symbols) { $symSet = @($Symbols) }
        if ($symSet.Count -eq 0) { Write-Warning "warm $($w.Id): no symbols to drive; skipping warm battery" }
        else {
            Write-Host "=== WARM $($w.Id) (MCP session, $($symSet.Count) symbols) ===" -ForegroundColor Cyan
            $warmById[$w.Id] = Invoke-WarmBattery $w.Path $McpExe $symSet $WarmIters
            if ($warmById[$w.Id]) { Write-Host ("  warm: search={0}ms def={1}ms sym={2}ms refs={3}ms (cold-first {4}ms)" -f `
                $warmById[$w.Id].searchMs, $warmById[$w.Id].defMs, $warmById[$w.Id].symMs, $warmById[$w.Id].refsMs, $warmById[$w.Id].refsColdMs) }
        }
    }
}

# Aggregate to per-repo medians.
function Med($vals) {
    $s = @($vals | Where-Object { $_ -ne $null } | Sort-Object)
    if (-not $s.Count) { return 0 }
    return [int]$s[[int][math]::Floor(($s.Count - 1) / 2)]
}
$summary = foreach ($w in $work) {
    $f = Join-Path $OutDir "matrix-$($w.Id).json"
    if (-not (Test-Path $f)) { continue }
    $b = Get-Content $f -Raw | ConvertFrom-Json
    $rows = @($b.rows)
    $filesNum = $null
    if ($b.files -and $b.files -match '([\d,]+)\s*$') { $filesNum = $Matches[1] }
    $warm = $warmById[$w.Id]
    [pscustomobject]@{
        id             = $w.Id
        lang           = $w.Lang
        files          = $filesNum
        indexMs        = $indexMsById[$w.Id]
        searchMs       = Med ($rows | ForEach-Object { $_.SearchMs })
        defMs          = Med ($rows | ForEach-Object { $_.DefMs })
        symMs          = Med ($rows | ForEach-Object { $_.SymMs })
        refsMs         = Med ($rows | ForEach-Object { $_.RefMs })
        refsMaxMs      = [int](($rows | ForEach-Object { $_.RefMs } | Measure-Object -Maximum).Maximum)
        refsMedTotal   = Med ($rows | ForEach-Object { $_.RefTotal })
        nSymbols       = $rows.Count
        warmSearchMs   = if ($warm) { $warm.searchMs } else { $null }
        warmDefMs      = if ($warm) { $warm.defMs } else { $null }
        warmSymMs      = if ($warm) { $warm.symMs } else { $null }
        warmRefsMs     = if ($warm) { $warm.refsMs } else { $null }
        warmRefsMaxMs  = if ($warm) { $warm.refsMaxMs } else { $null }
        warmRefsColdMs = if ($warm) { $warm.refsColdMs } else { $null }
    }
}

# Emit: console table, JSON, and a paste-ready Markdown matrix.
Write-Host "`n=== PER-TOOL MATRIX (cold CLI / warm MCP-session, median latency) ===" -ForegroundColor Green
$cols = if ($NoWarm) { 'id','lang','files','indexMs','searchMs','defMs','symMs','refsMs','refsMaxMs' }
        else { 'id','lang','files','indexMs','searchMs','warmSearchMs','defMs','warmDefMs','symMs','warmSymMs','refsMs','warmRefsMs','refsMaxMs','warmRefsMaxMs','warmRefsColdMs' }
$summary | Select-Object $cols | Format-Table -AutoSize | Out-String -Width 320 | Write-Host

$summaryPath = Join-Path $OutDir 'matrix-summary.json'
$summary | ConvertTo-Json -Depth 4 | Out-File $summaryPath -Encoding utf8

# Each tool cell is "cold / warm" (ASCII only, so it renders identically on GitHub). Cold = a fresh CLI
# process per query; warm = steady state over one long-lived MCP session.
function Cell($cold, $warm) {
    $c = if ($null -ne $cold) { "$cold ms" } else { '-' }
    if ($NoWarm) { return $c }
    $wv = if ($null -ne $warm) { "$warm ms" } else { 'n/a' }
    return "$c / $wv"
}
$md = New-Object System.Text.StringBuilder
if ($NoWarm) {
    [void]$md.AppendLine('| Repo | Lang | Files | Build | search_code | find_definition | search_symbols | find_references | refs max |')
    [void]$md.AppendLine('|---|---|--:|--:|--:|--:|--:|--:|--:|')
} else {
    [void]$md.AppendLine('_Each tool cell is **cold** (fresh CLI process: launch + mmap-open, worst case) / **warm** (steady-state over one long-lived MCP session - the path an agent actually uses). refs 1st-call is the one-time semantic analyzer build paid on the session''s first find_references._')
    [void]$md.AppendLine('')
    [void]$md.AppendLine('| Repo | Lang | Files | Build | search_code | find_definition | search_symbols | find_references | refs max | refs 1st-call |')
    [void]$md.AppendLine('|---|---|--:|--:|--:|--:|--:|--:|--:|--:|')
}
foreach ($s in $summary) {
    $build = if ($null -ne $s.indexMs) { if ($s.indexMs -ge 1000) { '{0:N1} s' -f ($s.indexMs/1000) } else { "$($s.indexMs) ms" } } else { 'cached' }
    if ($NoWarm) {
        [void]$md.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |" -f `
            $s.id, $s.lang, $s.files, $build, (Cell $s.searchMs $null), (Cell $s.defMs $null), (Cell $s.symMs $null), (Cell $s.refsMs $null), (Cell $s.refsMaxMs $null)))
    } else {
        $coldFirst = if ($null -ne $s.warmRefsColdMs) { "$($s.warmRefsColdMs) ms" } else { 'n/a' }
        [void]$md.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |" -f `
            $s.id, $s.lang, $s.files, $build, (Cell $s.searchMs $s.warmSearchMs), (Cell $s.defMs $s.warmDefMs), (Cell $s.symMs $s.warmSymMs), (Cell $s.refsMs $s.warmRefsMs), (Cell $s.refsMaxMs $s.warmRefsMaxMs), $coldFirst))
    }
}
$mdPath = Join-Path $OutDir 'perf-matrix.md'
$md.ToString() | Out-File $mdPath -Encoding utf8
Write-Host $md.ToString()
Write-Host "summary: $summaryPath" -ForegroundColor DarkGray
Write-Host "markdown: $mdPath" -ForegroundColor DarkGray
