<#
.SYNOPSIS
  End-to-end scenarios the unit tests can't reach, run on THIS box the way a real workstation uses CodeCompass:
  real MCP servers over stdio (the path Claude Code and Codex use), the real CLI, real UNC paths and links.

.DESCRIPTION
  Builds small throwaway repos under %TEMP%, then runs:

    A  Two sessions, one repo   - two live MCP servers on the same repo; a file edit is seen by both; concurrent
                                  find_references from both succeed; the index is healthy afterwards.
    B  Claude vs Codex root form - the same repo given with and without a trailing separator uses ONE cache
                                  (Codex and Claude spell the root differently); Codex registration checked if
                                  the codex CLI is installed.
    C  UNC                      - the repo through \\localhost\<drive>$ (a real SMB path on this box): index,
                                  doctor reports a network path, search + references over a live MCP session.
    D  Focus                    - a primary + a linked root; manage_links focus scopes search/references to one
                                  root and says so; clearing focus restores both.
    E  Links out of the repo    - a junction (and a file symlink, if this account may create one) pointing
                                  OUTSIDE the repo: its target's content must never be indexed or served.

  Every step prints PASS/FAIL; the script exits nonzero if any step failed. Caches it created are removed.
  Not heavy (small repos, a few processes) - no ComputeWarden lease needed.

.PARAMETER Bin
  Folder holding CodeCompass.Cli.exe and CodeCompass.Mcp.exe. Default: plugin\bin next to this repo.

.PARAMETER Only
  Run a subset, e.g. -Only A,D.

.EXAMPLE
  .\scripts\scenario-test.ps1
  .\scripts\scenario-test.ps1 -Only C
#>
[CmdletBinding()]
param(
    [string]$Bin,
    [string]$CliExe,   # override just the CLI (e.g. a Debug build whose server folder has no CLI next to it)
    [string[]]$Only,
    [int]$WatchWaitSec = 30
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Bin) { $Bin = Join-Path $repoRoot 'plugin\bin' }
$Cli = if ($CliExe) { $CliExe } else { Join-Path $Bin 'CodeCompass.Cli.exe' }
$Mcp = Join-Path $Bin 'CodeCompass.Mcp.exe'
foreach ($p in $Cli, $Mcp) { if (-not (Test-Path $p)) { throw "not found: $p (run build-plugin.ps1, or pass -Bin)" } }

$failures = New-Object System.Collections.Generic.List[string]
function Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok($t)      { Write-Host "  PASS  $t" -ForegroundColor Green }
function Bad($t)     { Write-Host "  FAIL  $t" -ForegroundColor Red; $failures.Add($t) }
function Skip($t)    { Write-Host "  SKIP  $t" -ForegroundColor Yellow }
function Check($name, [bool]$cond, [string]$detail = '') { if ($cond) { Ok $name } else { Bad ("$name" + $(if ($detail) { " -- $detail" } else { '' })) } }
function Want($id) { -not $Only -or ($Only -contains $id) }

# ---- CLI --------------------------------------------------------------------------------------------------------

function Invoke-Cli([string[]]$CliArgs, [int]$TimeoutSec = 300, [hashtable]$Env) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Cli
    $psi.Arguments = ($CliArgs | ForEach-Object { if ($_ -match '[\s"]' -or $_.EndsWith('\')) { '"' + ($_ -replace '\\$', '\\') + '"' } else { $_ } }) -join ' '
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    if ($Env) { foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[$k] = $Env[$k] } }
    $p = [System.Diagnostics.Process]::Start($psi)
    $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch {}; return [pscustomobject]@{ Code = -1; Out = ''; Err = 'TIMEOUT' } }
    $p.WaitForExit()
    [pscustomobject]@{ Code = $p.ExitCode; Out = $o.Result; Err = $e.Result }
}

# ---- MCP sessions (one object per live server) --------------------------------------------------------------------

function Start-Session([string]$Cwd, [hashtable]$Env) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Mcp; $psi.WorkingDirectory = $Cwd
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    if ($Env) { foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[$k] = $Env[$k] } }
    $proc = [System.Diagnostics.Process]::Start($psi)
    $s = [pscustomobject]@{ Proc = $proc; Err = $proc.StandardError.ReadToEndAsync(); NextId = 10; Pending = $null }
    $null = Invoke-Rpc $s 'initialize' '{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"scenario-test","version":"1"}}' 60
    $s.Proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}'); $s.Proc.StandardInput.Flush()
    return $s
}

function Send-Request($s, [string]$method, [string]$paramsJson) {
    $id = $s.NextId; $s.NextId++
    $s.Proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":' + $id + ',"method":"' + $method + '","params":' + $paramsJson + '}')
    $s.Proc.StandardInput.Flush()
    return $id
}

function Receive-Response($s, [int]$id, [int]$TimeoutSec = 300) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not $s.Pending) { $s.Pending = $s.Proc.StandardOutput.ReadLineAsync() }
        if (-not $s.Pending.Wait(250)) { continue }
        $line = $s.Pending.Result; $s.Pending = $null
        if ($null -eq $line) { return $null } # server exited
        try { $obj = $line | ConvertFrom-Json } catch { continue }
        if ($obj.PSObject.Properties.Name -contains 'id' -and $obj.id -eq $id) { return $obj }
    }
    return $null
}

function Invoke-Rpc($s, [string]$method, [string]$paramsJson, [int]$TimeoutSec = 300) {
    Receive-Response $s (Send-Request $s $method $paramsJson) $TimeoutSec
}

function ToolText($resp) {
    if ($null -eq $resp) { return $null }
    if ($resp.PSObject.Properties.Name -contains 'error' -and $resp.error) { return "RPC ERROR: " + ($resp.error | ConvertTo-Json -Compress) }
    ($resp.result.content | ForEach-Object { $_.text }) -join "`n"
}

function Call-Tool($s, [string]$tool, [hashtable]$toolArgs, [int]$TimeoutSec = 300) {
    $p = @{ name = $tool; arguments = $toolArgs } | ConvertTo-Json -Compress -Depth 5
    ToolText (Invoke-Rpc $s 'tools/call' $p $TimeoutSec)
}

function Stop-Session($s) {
    if ($null -eq $s) { return }
    try { $s.Proc.StandardInput.Close() } catch {}
    if (-not $s.Proc.WaitForExit(15000)) { try { $s.Proc.Kill() } catch {} }
}

# Poll a session until a search finds $token (the file watcher is asynchronous).
function Wait-Search($s, [string]$token, [int]$Sec) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Sec)
    do {
        $t = Call-Tool $s 'search_code' @{ query = $token }
        if ($t -and $t -match [regex]::Escape($token) -and $t -notmatch 'No matches') { return $true }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

# ---- fixtures ------------------------------------------------------------------------------------------------------

$work = Join-Path $env:TEMP ("cc-scenario-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$cleanupRoots = New-Object System.Collections.Generic.List[string]

function New-Repo([string]$name, [string]$tag) {
    $r = Join-Path $work $name
    New-Item -ItemType Directory -Force -Path (Join-Path $r 'src'), (Join-Path $r 'include\lib') | Out-Null
    Set-Content -Encoding UTF8 (Join-Path $r 'src\Widget.cs') @"
namespace Demo$tag;
public class Widget$tag { public int Spin$tag(int x) => x + 1; }
public class User$tag { public int Go() { var w = new Widget$tag(); return w.Spin$tag(1) + w.Spin$tag(2); } }
"@
    Set-Content -Encoding UTF8 (Join-Path $r 'include\lib\gear.h') "#pragma once`nint gear_turn_$tag(int);`n"
    Set-Content -Encoding UTF8 (Join-Path $r 'src\gear.c') "#include `"lib/gear.h`"`nint gear_turn_$tag(int x){ return x * 2; }`n"
    Set-Content -Encoding UTF8 (Join-Path $r 'src\use.c') "#include `"lib/gear.h`"`nint use_$tag(void){ return gear_turn_$tag(3) + gear_turn_$tag(4); }`n"
    $cleanupRoots.Add($r)
    return $r
}

try {
    # =========================================================================================================== A
    if (Want 'A') {
        Section 'A  Two sessions, one repo'
        $a = New-Repo 'twosess' 'A'
        $ix = Invoke-Cli @('index', $a)
        Check 'A1 index builds' ($ix.Code -eq 0) $ix.Err
        $s1 = Start-Session $a; $s2 = Start-Session $a
        try {
            $t1 = Call-Tool $s1 'search_code' @{ query = 'SpinA' }; $t2 = Call-Tool $s2 'search_code' @{ query = 'SpinA' }
            Check 'A2 both sessions answer the same search' ($t1 -and $t1 -eq $t2 -and $t1 -match 'Widget\.cs') "s1=[$t1] s2=[$t2]"

            $token = 'FreshToken' + (Get-Random)
            Add-Content -Encoding UTF8 (Join-Path $a 'src\Widget.cs') "// $token"
            Check 'A3 session 1 sees the edit' (Wait-Search $s1 $token $WatchWaitSec)
            Check 'A4 session 2 sees the edit' (Wait-Search $s2 $token $WatchWaitSec)

            # Concurrent semantic queries from both servers (C# and C/C++), interleaved on the wire.
            $ok = $true
            for ($i = 0; $i -lt 3; $i++) {
                $id1 = Send-Request $s1 'tools/call' '{"name":"find_references","arguments":{"name":"SpinA"}}'
                $id2 = Send-Request $s2 'tools/call' '{"name":"find_references","arguments":{"name":"gear_turn_A"}}'
                $r1 = ToolText (Receive-Response $s1 $id1); $r2 = ToolText (Receive-Response $s2 $id2)
                if (-not ($r1 -match 'Widget\.cs' -and $r2 -match 'use\.c')) { $ok = $false; Write-Host "    r1=[$r1]`n    r2=[$r2]" }
            }
            Check 'A5 concurrent find_references from both sessions succeed' $ok
            $refs = Call-Tool $s1 'find_references' @{ name = 'gear_turn_A' }
            Check 'A6 C/C++ refs are found by name (calls, not the definition)' ($refs -match 'use\.c:2:' -and $refs -notmatch 'gear\.c:2:' -and $refs -match 'matched by NAME') $refs
        }
        finally { Stop-Session $s1; Stop-Session $s2 }
        $doc = Invoke-Cli @('doctor', $a)
        Check 'A7 index healthy after both sessions exit' ($doc.Out -match 'status:\s+ready') ($doc.Out + $doc.Err)
        $cs = Invoke-Cli @('search', $a, $token)
        Check 'A8 the edit is in the persisted index' ($cs.Out -match [regex]::Escape($token))
    }

    # =========================================================================================================== B
    if (Want 'B') {
        Section 'B  Claude vs Codex root form'
        $b = New-Repo 'rootform' 'B'
        $null = Invoke-Cli @('index', $b)
        $d1 = Invoke-Cli @('doctor', $b); $d2 = Invoke-Cli @('doctor', ($b + '\'))
        $c1 = if ($d1.Out -match 'cache dir:\s+(\S+)') { $Matches[1] } else { 'none' }
        $c2 = if ($d2.Out -match 'cache dir:\s+(\S+)') { $Matches[1] } else { 'none2' }
        Check 'B1 trailing separator maps to the same cache' ($c1 -eq $c2) "plain=$c1 trailing=$c2"
        $sx = Start-Session ($b + '\')
        try { Check 'B2 a session started from the trailing-separator path answers' ((Call-Tool $sx 'search_code' @{ query = 'SpinB' }) -match 'Widget\.cs') }
        finally { Stop-Session $sx }

        $codex = Get-Command codex -ErrorAction SilentlyContinue
        if ($codex) {
            $list = (& codex mcp list 2>&1 | Out-String)
            if ($list -match 'codecompass') {
                Check 'B3 Codex has codecompass registered' $true
                $exe = [regex]::Match($list, '(?i)\S*CodeCompass\.Mcp\.exe').Value
                if ($exe) { Check 'B4 the registered Codex server binary exists' (Test-Path $exe) $exe } else { Skip 'B4 could not read the registered binary path from codex mcp list' }
            } else { Bad 'B3 Codex has codecompass registered' }
        } else { Skip 'B3/B4 codex CLI not installed on this box' }
    }

    # =========================================================================================================== C
    if (Want 'C') {
        Section 'C  UNC (\\localhost admin share)'
        $c = New-Repo 'unc' 'C'
        $drive = $c.Substring(0, 1)
        $unc = '\\localhost\' + $drive + '$' + $c.Substring(2)
        if (-not (Test-Path $unc)) { Skip "C  $unc is not reachable (admin share disabled?) - set CODECOMPASS_FORCE_NETWORK=1 for the network code path instead" }
        else {
            $cleanupRoots.Add($unc)
            $ix = Invoke-Cli @('index', $unc)
            Check 'C1 index over UNC' ($ix.Code -eq 0) $ix.Err
            $doc = Invoke-Cli @('doctor', $unc)
            Check 'C2 doctor reports a network path' ($doc.Out -match 'network path:\s+True') $doc.Out
            $su = Start-Session $unc
            try {
                Check 'C3 search over UNC' ((Call-Tool $su 'search_code' @{ query = 'SpinC' }) -match 'Widget\.cs')
                $r = Call-Tool $su 'find_references' @{ name = 'SpinC' }
                Check 'C4 C# references over UNC' ($r -match 'Widget\.cs') $r
                $r = Call-Tool $su 'find_references' @{ name = 'gear_turn_C' }
                Check 'C5 C/C++ references over UNC' ($r -match 'use\.c:2:' -and $r -match 'matched by NAME') $r
                $token = 'UncToken' + (Get-Random)
                Add-Content -Encoding UTF8 (Join-Path $c 'src\use.c') "// $token"
                Check 'C6 an edit is picked up over UNC' (Wait-Search $su $token ($WatchWaitSec * 2))
            }
            finally { Stop-Session $su }
        }
    }

    # =========================================================================================================== D
    if (Want 'D') {
        Section 'D  Focus across linked roots'
        $p = New-Repo 'focus-primary' 'P'
        $l = New-Repo 'focus-linked' 'L'
        # A token in both roots, and a C# symbol referenced in both.
        Add-Content -Encoding UTF8 (Join-Path $p 'src\Widget.cs') '// SharedMarker'
        Add-Content -Encoding UTF8 (Join-Path $l 'src\Widget.cs') '// SharedMarker'
        $null = Invoke-Cli @('index', $p); $null = Invoke-Cli @('index', $l)
        $sd = Start-Session $p
        try {
            $add = Call-Tool $sd 'manage_links' @{ action = 'add'; path = $l }
            Check 'D1 link the second root' ($add -and $add -notmatch '(?i)error|fail') $add
            $both = Call-Tool $sd 'search_code' @{ query = 'SharedMarker' }
            Check 'D2 unfocused search covers both roots' ($both -match 'focus-primary' -or ($both -split "`n" | Where-Object { $_ -match 'Widget\.cs' }).Count -ge 2) $both
            $f = Call-Tool $sd 'manage_links' @{ action = 'focus'; path = 'focus-linked' }
            Check 'D3 focus accepted' ($f -match '(?i)focus') $f
            $one = Call-Tool $sd 'search_code' @{ query = 'SharedMarker' }
            $hits = @($one -split "`n" | Where-Object { $_ -match 'Widget\.cs' })
            Check 'D4 focused search returns only the focused root' ($hits.Count -eq 1) $one
            Check 'D5 focused results say they are scoped' ($one -match '(?i)focus') $one
            $r = Call-Tool $sd 'find_references' @{ name = 'gear_turn_P' }
            Check 'D6 references in the unfocused root are not returned' ($r -notmatch 'use\.c:') $r
            $clear = Call-Tool $sd 'manage_links' @{ action = 'focus' }
            $again = Call-Tool $sd 'search_code' @{ query = 'SharedMarker' }
            Check 'D7 clearing focus restores both roots' (@($again -split "`n" | Where-Object { $_ -match 'Widget\.cs' }).Count -ge 2) $again
            $null = Call-Tool $sd 'manage_links' @{ action = 'remove'; path = $l }
        }
        finally { Stop-Session $sd }
    }

    # =========================================================================================================== E
    if (Want 'E') {
        Section 'E  Links pointing out of the repo'
        $e = New-Repo 'links' 'E'
        $outside = Join-Path $work 'outside'
        New-Item -ItemType Directory -Force -Path $outside | Out-Null
        $secret = 'OutsideSecret' + (Get-Random)
        Set-Content -Encoding UTF8 (Join-Path $outside 'secret.c') "int $secret = 1;`n"
        cmd /c mklink /J "$(Join-Path $e 'src\junction')" "$outside" | Out-Null
        $fileLink = $false
        try { New-Item -ItemType SymbolicLink -Path (Join-Path $e 'src\linked.c') -Target (Join-Path $outside 'secret.c') -ErrorAction Stop | Out-Null; $fileLink = $true } catch { }
        $null = Invoke-Cli @('index', $e)
        $se = Invoke-Cli @('search', $e, $secret)
        Check 'E1 content behind a junction is not indexed' ($se.Out -notmatch [regex]::Escape($secret)) $se.Out
        if ($fileLink) { Check 'E2 content behind a file symlink is not indexed' ($se.Out -notmatch 'linked\.c') $se.Out }
        else { Skip 'E2 this account cannot create file symlinks (needs Developer Mode or admin)' }
        $sm = Start-Session $e
        try { $t = Call-Tool $sm 'search_code' @{ query = $secret }; Check 'E3 a live session does not serve it either' ($t -notmatch [regex]::Escape($secret) -or $t -match 'No matches') $t }
        finally { Stop-Session $sm }
        cmd /c rmdir "$(Join-Path $e 'src\junction')" | Out-Null
    }
}
finally {
    foreach ($r in $cleanupRoots) { $null = Invoke-Cli @('cache', 'clear', $r) 60 }
    try { Remove-Item -Recurse -Force $work -ErrorAction Stop } catch { Write-Warning "could not remove $work : $($_.Exception.Message)" }
}

Write-Host ''
if ($failures.Count -eq 0) { Write-Host 'ALL SCENARIOS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$($failures.Count) FAILED:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
