<#
.SYNOPSIS
  Warm-session memory probe: drive the CodeCompass MCP server (ONE long-lived process) through a battery
  of find_references calls over stdio, sampling the server's RSS across the WHOLE session. This is the
  companion to probe-repo.ps1 - the CLI probe runs a fresh process per query and structurally CANNOT see
  cross-query accumulation (a bounded query grows cold->budget->exits, indistinguishable from a ratchet).
  Only a persistent server reveals the dangerous shape: does baseline RSS climb query-over-query, or plateau?

.WHY
  176's bug (and 179's higher ceiling) live in the warm server a real user drives via Claude/Codex. This
  measures exactly that: session peak, per-query cost, and the cross-query baseline trend (the ratchet signal).
  External ceiling-kill so a runaway can never take the box down.

.EXAMPLE
  .\probe-mcp.ps1 -Repo D:\big -Symbols hot_helper,dma_start,reg_write
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Repo,
    [string]$Exe,                              # CodeCompass.Mcp.exe; default: plugin\bin next to this script
    [string[]]$Symbols,
    [string]$SymbolsFile,
    [int]$AutoTop = 5,                         # if no -Symbols, auto-pick this many broadest C/C++ symbols
    [int]$AutoPool = 120,                      # candidate pool to rank when auto-picking
    [int]$MaxResults = 100,
    [int]$CeilMB = 0,                          # kill the server above this RSS (0 => 70% physical RAM)
    [int]$IntervalMs = 200,                    # RSS sample cadence (also the response poll granularity)
    [int]$QueryTimeoutSec = 240,               # per-call hang guard
    [int]$IdleGapSec = 0,                      # sleep between queries (>= CODECOMPASS_SEMANTIC_IDLE_MIN window => tests eviction)
    [int]$RepeatFirst = 3,                     # re-run the first symbol this many times at the end: an IDENTICAL
                                               # query re-parses the SAME files, so if RSS keeps climbing across
                                               # the repeats it's a real leak; if it plateaus it was warm-up.
    [switch]$KeepNames,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
if (-not $Exe)    { $Exe = Join-Path $scriptDir 'plugin\bin\CodeCompass.Mcp.exe' }
if (-not $OutDir) { $OutDir = $scriptDir }
if (-not (Test-Path $Exe))  { throw "MCP exe not found: $Exe" }
if (-not (Test-Path $Repo)) { throw "repo not found: $Repo" }
# .ProviderPath, not .Path: on a UNC path .Path returns the provider-qualified form
# (Microsoft.PowerShell.Core\FileSystem::\\server\share) which .NET Process.Start rejects as a
# WorkingDirectory ("directory name is invalid"), so the probe couldn't run against a network repo at all.
$Repo = (Resolve-Path $Repo).ProviderPath
if ($CeilMB -le 0) { $CeilMB = [int]([math]::Floor(((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB) * 0.70)) }

$symbolSet = @()
if ($SymbolsFile) { $symbolSet += @(Get-Content $SymbolsFile | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
if ($Symbols)     { $symbolSet += @($Symbols | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$symbolSet = @($symbolSet | Select-Object -Unique)

# Auto-derive the broadest C/C++ symbols (via the CLI - cheap, no warm server needed) if none given.
if ($symbolSet.Count -eq 0) {
    $cli = Join-Path (Split-Path $Exe) 'CodeCompass.Cli.exe'
    if (-not (Test-Path $cli)) { throw "no -Symbols given and CLI not found for auto-derive: $cli" }
    Write-Host "auto-deriving the $AutoTop broadest C/C++ symbols..." -ForegroundColor Yellow
    $names = New-Object System.Collections.Generic.HashSet[string]
    foreach ($c in @([char[]]'etaoinshrdlcum_')) {
        if ($names.Count -ge $AutoPool) { break }
        foreach ($ln in (& $cli symbols $Repo "$c" 2>$null)) {
            if ($ln -match '\.(c|h|cc|cpp|cxx):\d+:\d+:\s+\S+\s+(\S+)\s*$') { [void]$names.Add($Matches[2]); if ($names.Count -ge $AutoPool) { break } }
        }
    }
    $ranked = foreach ($nm in $names) {
        $hits = @(& $cli search $Repo $nm 2>$null | Select-String -Pattern '\.(c|h|cc|cpp|cxx):' ).Count
        [pscustomobject]@{ Name = $nm; Hits = $hits }
    }
    $symbolSet = @($ranked | Sort-Object Hits -Descending | Select-Object -First $AutoTop -ExpandProperty Name)
    Write-Host ("auto-picked: " + ($symbolSet -join ', ')) -ForegroundColor Yellow
}
if ($symbolSet.Count -eq 0) { throw "no symbols to probe (auto-derive found none; pass -Symbols)" }

function Get-Hash4([string]$s) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    -join (($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($s)))[0..1] | ForEach-Object { $_.ToString('x2') })
}

Write-Host "mcp-probe: repo=$Repo  ceil=${CeilMB}MB  symbols=$($symbolSet.Count)" -ForegroundColor Cyan

# --- launch ONE long-lived MCP server, cwd = the repo (server serves its working directory) ---
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = $Repo
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo = $psi
$sw = [System.Diagnostics.Stopwatch]::StartNew()
[void]$proc.Start()
$errTask = $proc.StandardError.ReadToEndAsync()   # drain stderr (server logs) so it can't fill the pipe

$script:peakMB = 0.0; $script:curve = New-Object System.Collections.Generic.List[object]; $script:killed = $false
function Sample-RSS {
    try { $proc.Refresh(); $ws = $proc.WorkingSet64 / 1MB } catch { return }
    if ($ws -gt $script:peakMB) { $script:peakMB = $ws }
    $script:curve.Add([pscustomobject]@{ t = [math]::Round($sw.Elapsed.TotalSeconds, 1); mb = [math]::Round($ws, 1) })
    if ($ws -gt $CeilMB -and -not $script:killed) { $script:killed = $true; try { $proc.Kill() } catch {} }
}
function Cur-RSS { try { $proc.Refresh(); return [math]::Round($proc.WorkingSet64 / 1MB, 1) } catch { return $null } }

# Write one JSON-RPC line; read stdout lines until the response with $id arrives, sampling RSS while we wait.
function Send-Rpc([string]$json, [int]$id, [int]$timeoutSec) {
    $proc.StandardInput.WriteLine($json); $proc.StandardInput.Flush()
    if ($id -lt 0) { return $null }   # notification: no response expected
    $deadline = $sw.Elapsed.TotalSeconds + $timeoutSec
    while ($true) {
        $rt = $proc.StandardOutput.ReadLineAsync()
        while (-not $rt.Wait($IntervalMs)) {
            Sample-RSS
            if ($script:killed -or $sw.Elapsed.TotalSeconds -gt $deadline) { return $null }
        }
        $line = $rt.Result
        if ($null -eq $line) { return $null }   # EOF
        Sample-RSS
        $obj = $null; try { $obj = $line | ConvertFrom-Json } catch { continue }  # skip any non-JSON line
        if ($obj.PSObject.Properties.Name -contains 'id' -and $obj.id -eq $id) { return $obj }
    }
}

# --- handshake ---
$null = Send-Rpc '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe-mcp","version":"1"}}}' 1 30
$null = Send-Rpc '{"jsonrpc":"2.0","method":"notifications/initialized"}' -1 0

function Refs-Text($obj) {
    if ($null -eq $obj -or $null -eq $obj.result) { return $null }
    $c = $obj.result.content
    if ($c -and $c.Count -ge 1) { return ($c | ForEach-Object { $_.text }) -join "`n" }
    return ($obj.result | ConvertTo-Json -Depth 6)
}

# --- battery: one warm session, find_references per symbol, tracking the cross-query baseline ---
$id = 100
$first = $true
$rows = foreach ($sym in $symbolSet) {
    if ($IdleGapSec -gt 0 -and -not $first) {
        # Idle between queries so the server's idle-eviction timer can fire and (maybe) release the analyzers.
        # Sample RSS across the gap so the curve shows whether the baseline drops back.
        $gapEnd = $sw.Elapsed.TotalSeconds + $IdleGapSec
        while ($sw.Elapsed.TotalSeconds -lt $gapEnd -and -not $script:killed) { Sample-RSS; Start-Sleep -Milliseconds $IntervalMs }
    }
    $first = $false
    $before = Cur-RSS                                   # session RSS at the START of this query (ratchet signal)
    $t0 = $sw.Elapsed.TotalSeconds
    $args = '{"name":"' + ($sym -replace '"', '\"') + '","maxResults":' + $MaxResults + '}'
    $req = '{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":{"name":"find_references","arguments":' + $args + '}}'
    $resp = Send-Rpc $req $id ($QueryTimeoutSec)
    $ms = [int](($sw.Elapsed.TotalSeconds - $t0) * 1000)
    $after = Cur-RSS
    $txt = Refs-Text $resp
    $refCount = if ($txt) { @([regex]::Matches($txt, ':\d+:\d+:')).Count } else { 0 }
    # Since v1.0.239 C/C++ references are a name search: there is no memory stop or parsed/candidate coverage any more
    # (kept as columns, always false/null), and the by-name note replaces the old "lexical" caveat.
    $memStop = $false
    $incomplete = $null
    $lexNote = [bool]($txt -match 'matched by NAME')
    $timedOut = ($null -eq $resp)
    Write-Host ("  {0,-16} refs={1,-5} {2,6}ms  before={3,6}MB after={4,6}MB{5}" -f $(if($KeepNames){$sym}else{Get-Hash4 $sym}), $refCount, $ms, $before, $after, $(if($timedOut){' TIMEOUT'}elseif($script:killed){' KILLED'}else{''})) -ForegroundColor DarkGray
    [pscustomobject]@{ Id = if ($KeepNames) { $sym } else { Get-Hash4 $sym }; Refs = $refCount; Ms = $ms; BeforeMB = $before; AfterMB = $after; MemStop = $memStop; Cov = $incomplete; Lexical = $lexNote; TimedOut = $timedOut }
    $id++
    if ($script:killed) { break }
}

# Re-run the FIRST symbol RepeatFirst times at the end. Identical query => same candidate files re-parsed;
# if RSS keeps climbing across these repeats, native memory isn't being reclaimed (a real leak); if it
# plateaus, the earlier baseline climb was warm-up / distinct-query cost, not a ratchet.
$repeats = New-Object System.Collections.Generic.List[object]
if (-not $script:killed -and $symbolSet.Count -ge 1) {
    $sym = $symbolSet[0]
    for ($k = 0; $k -lt [math]::Max(1, $RepeatFirst); $k++) {
        if ($script:killed) { break }
        $before = Cur-RSS; $t0 = $sw.Elapsed.TotalSeconds
        $rargs = '{"name":"' + ($sym -replace '"', '\"') + '","maxResults":' + $MaxResults + '}'
        $resp = Send-Rpc ('{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":{"name":"find_references","arguments":' + $rargs + '}}') $id $QueryTimeoutSec
        $txt = Refs-Text $resp
        $repeats.Add([pscustomobject]@{ Id = $(if ($KeepNames) { $sym } else { Get-Hash4 $sym }); Refs = $(if ($txt) { @([regex]::Matches($txt, ':\d+:\d+:')).Count } else { 0 }); Ms = [int](($sw.Elapsed.TotalSeconds - $t0) * 1000); BeforeMB = $before; AfterMB = (Cur-RSS) })
        $id++
    }
}
$repeat = if ($repeats.Count -gt 0) { $repeats[0] } else { $null }  # first repeat (back-compat for the report/JSON)

# --- shut the server down cleanly ---
try { $proc.StandardInput.Close() } catch {}
if (-not $proc.WaitForExit(5000)) { try { $proc.Kill() } catch {} }
$sw.Stop()

# --- leak analysis: does an IDENTICAL re-query keep growing RSS, or plateau? ---
# The whole-battery climb mixes distinct-query cost + sidecar-cache warm-up (bounded by the cache budget)
# with any real leak, so it over-triggers (a few hundred MB of cache warm-up looks like a ratchet). The
# clean signal is the baseline drift ACROSS the identical repeats of the FIRST symbol: same candidate files
# re-parsed each time, so if the baseline keeps climbing the native memory isn't being reclaimed (a real
# leak); if it plateaus, the earlier climb was warm-up.
$befores = @($rows | Where-Object { $null -ne $_.BeforeMB } | ForEach-Object { $_.BeforeMB })
$baseFirst = if ($befores.Count -ge 1) { $befores[0] } else { 0 }
$baseLast = if ($befores.Count -ge 1) { $befores[-1] } else { 0 }
$baseClimb = [math]::Round($baseLast - $baseFirst, 0)
$repBefore = @($repeats | ForEach-Object { $_.BeforeMB })
$repeatClimb = if ($repBefore.Count -ge 2) { [math]::Round($repBefore[-1] - $repBefore[0], 0) } else { $null }
$ratchet = ($null -ne $repeatClimb) -and ($repeatClimb -gt 100) # identical re-query still growing => not reclaimed

# --- report (anonymized, Discord-sized) ---
$version = ((& (Join-Path (Split-Path $Exe) 'CodeCompass.Cli.exe') version) 2>$null)
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('```')
[void]$sb.AppendLine("CodeCompass MCP-session probe - $version   repo=$(Get-Hash4 $Repo)(hashed)")
[void]$sb.AppendLine("one warm server, $($rows.Count) find_references calls  ceiling ${CeilMB}MB")
[void]$sb.AppendLine('')
[void]$sb.AppendLine(" id   refs   ms   beforeMB afterMB cov     note")
foreach ($r in $rows) {
    $note = @(); if ($r.MemStop) { $note += 'memstop' }; if ($r.Lexical) { $note += 'lex' }; if ($r.TimedOut) { $note += 'TIMEOUT' }
    [void]$sb.AppendLine(("{0} {1,5} {2,6} {3,8} {4,7} {5,-7} {6}" -f $r.Id, $r.Refs, $r.Ms, $r.BeforeMB, $r.AfterMB, $(if($r.Cov){$r.Cov}else{'-'}), ($note -join ',')))
}
foreach ($rp in $repeats) { [void]$sb.AppendLine(("{0} {1,5} {2,6} {3,8} {4,7} {5,-7} repeat-of-first" -f $rp.Id, $rp.Refs, $rp.Ms, $rp.BeforeMB, $rp.AfterMB, '-')) }
[void]$sb.AppendLine("session peak RSS: $([int]$script:peakMB)MB$(if($script:killed){' - KILLED at ceiling'})")
$verdict = if ($script:killed) { 'KILLED - exceeded ceiling' }
    elseif ($null -eq $repeatClimb) { "inconclusive: run -RepeatFirst >= 2 to test for a leak (whole-session climb ${baseClimb}MB)" }
    elseif ($ratchet) { "LEAK: identical re-query grew baseline +${repeatClimb}MB across $($repeats.Count) repeats - native memory not reclaimed (whole-session climb ${baseClimb}MB)" }
    else { "no leak: identical re-query plateaued (baseline +${repeatClimb}MB across $($repeats.Count) repeats); the ${baseClimb}MB whole-session climb was warm-up / distinct-query cost" }
[void]$sb.AppendLine("memory: $verdict")
if ($repeat) { [void]$sb.AppendLine("determinism: repeat refs $($repeat.Refs) vs first $($rows[0].Refs) ($(if($repeat.Refs -eq $rows[0].Refs){'match'}else{'DIFFER'}))") }
[void]$sb.AppendLine('```')
$report = $sb.ToString()

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$rh = Get-Hash4 $Repo
@{ version = $version; repoHash = $rh; ceilMB = $CeilMB; peakMB = $script:peakMB; killed = $script:killed; ratchet = $ratchet; baseClimbMB = $baseClimb; repeatClimbMB = $repeatClimb; rows = $rows; repeats = $repeats.ToArray(); repeat = $repeat; curve = $script:curve.ToArray() } |
    ConvertTo-Json -Depth 8 | Out-File (Join-Path $OutDir "probe-mcp-$rh-$stamp.json") -Encoding utf8
$report | Out-File (Join-Path $OutDir "probe-mcp-$rh-$stamp.txt") -Encoding utf8

Write-Host ""; Write-Host "=== paste this into Discord ===" -ForegroundColor Green; Write-Host $report
Write-Host ("report chars: {0}" -f $report.Length) -ForegroundColor DarkGray