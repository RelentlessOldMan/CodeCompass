<#
.SYNOPSIS
  Fleet health sweep: run the current CodeCompass build over a list of local repos and report how we
  stand - index success/time/peak-RSS, doctor health, and a sanity query battery per repo. One
  consolidated table + a rich local JSON. Read-only w.r.t. the repos (index caches live under LOCALAPPDATA).

.NOTES
  Machine-saturating (indexes many repos). Gate via ComputeWarden before running.
#>
[CmdletBinding()]
param(
    [string[]]$Repos,                          # repo paths; default = git repos under C:\Playground
    [string]$Exe,                              # CodeCompass.Cli.exe; default: plugin\bin next to this script
    [int]$CeilMB = 0,                          # kill a step above this RSS (0 => 60% physical RAM)
    [int]$IntervalMs = 200,
    [switch]$SkipIndex,                        # query existing indexes only
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
if (-not $Exe)    { $Exe = Join-Path $scriptDir 'plugin\bin\CodeCompass.Cli.exe' }
if (-not $OutDir) { $OutDir = $scriptDir }
if (-not (Test-Path $Exe)) { throw "CLI exe not found: $Exe" }
if ($CeilMB -le 0) { $CeilMB = [int]([math]::Floor(((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB) * 0.60)) }

if (-not $Repos -or $Repos.Count -eq 0) {
    $Repos = @(Get-ChildItem -Path 'C:\Playground' -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName '.git') -and $_.Name -ne 'TheMemoryKeeper_Backup_1' } |
        ForEach-Object { $_.FullName })
}
Write-Host "sweep: $($Repos.Count) repo(s)  ceil=${CeilMB}MB" -ForegroundColor Cyan

function Invoke-Sampled([string[]]$CliArgs) {
    $cmdline = ($CliArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe; $psi.Arguments = $cmdline
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $peakMB = 0.0; $killed = $false
    $p = New-Object System.Diagnostics.Process; $p.StartInfo = $psi
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); [void]$p.Start()
    $outTask = $p.StandardOutput.ReadToEndAsync(); $errTask = $p.StandardError.ReadToEndAsync()
    while (-not $p.HasExited) {
        try { $p.Refresh(); $ws = $p.WorkingSet64 / 1MB } catch { break }
        if ($ws -gt $peakMB) { $peakMB = $ws }
        if ($ws -gt $CeilMB) { $killed = $true; try { $p.Kill() } catch {}; break }
        Start-Sleep -Milliseconds $IntervalMs
    }
    $p.WaitForExit(); $sw.Stop()
    $so = try { $outTask.Result } catch { '' }; if ($null -eq $so) { $so = '' }
    $se = try { $errTask.Result } catch { '' }; if ($null -eq $se) { $se = '' }
    [pscustomobject]@{ ExitCode = $p.ExitCode; Ms = [int]$sw.Elapsed.TotalMilliseconds; PeakMB = [math]::Round($peakMB, 1); Killed = $killed; Stdout = $so; Stderr = $se }
}

# Pick a real symbol from the repo to drive def/refs (first symbol name from a few common-letter probes).
function Pick-Symbol([string]$repo) {
    foreach ($c in @('e', 'a', 'i', 's', 't', 'r')) {
        $r = Invoke-Sampled @('symbols', $repo, $c)
        foreach ($ln in ($r.Stdout -split "`n")) {
            if ($ln -match ':\d+:\d+:\s+\S+\s+(\S+)\s*$') { return $Matches[1] }
        }
    }
    return $null
}

$rows = foreach ($repo in $Repos) {
    $name = Split-Path $repo -Leaf
    Write-Host "  $name ..." -ForegroundColor DarkCyan -NoNewline
    $idxMs = $null; $idxPeak = $null; $idxKilled = $false; $idxErr = $null
    if (-not $SkipIndex) {
        $ix = Invoke-Sampled @('index', $repo)
        $idxMs = $ix.Ms; $idxPeak = $ix.PeakMB; $idxKilled = $ix.Killed
        if ($ix.ExitCode -ne 0) { $idxErr = "exit $($ix.ExitCode)" }
    }

    $doc = Invoke-Sampled @('doctor', $repo)
    function Grab($p) { foreach ($ln in ($doc.Stdout -split "`n")) { if ($ln -match $p) { return $ln.Trim() } } return $null }
    $filesLine = Grab 'files \(at build\):'   # doctor's line is "files (at build): 43,422"
    $files = if ($filesLine -match '([\d,]+)') { [int]($Matches[1] -replace ',', '') } else { $null }
    $warns = @(($doc.Stdout -split "`n") | Where-Object { $_ -match '\[WARN\]' }).Count
    $sidecar = Grab 'sidecar cache:'

    $sym = Pick-Symbol $repo
    $refCs = $null; $refCpp = $null; $refLex = $null; $refMs = $null; $refPeak = $null; $refKilled = $false; $parsed = $null; $cand = $null
    $searchMs = $null
    if ($sym) {
        $refs = Invoke-Sampled @('refs', $repo, $sym)
        if ($refs.Stderr -match '(\d+)\s+C#\s+\+\s+(\d+)\s+C/C\+\+ semantic\s+\+\s+(\d+)\s+lexical') { $refCs = [int]$Matches[1]; $refCpp = [int]$Matches[2]; $refLex = [int]$Matches[3] }
        if ($refs.Stdout -match '(\d+)/(\d+)\s+candidate C/C\+\+ file\(s\) parsed') { $parsed = [int]$Matches[1]; $cand = [int]$Matches[2] }
        $refMs = $refs.Ms; $refPeak = $refs.PeakMB; $refKilled = $refs.Killed
        $sr = Invoke-Sampled @('search', $repo, $sym); $searchMs = $sr.Ms
    }
    Write-Host " done ($($files) files, idx $([int]$idxMs)ms)" -ForegroundColor DarkGray

    [pscustomobject]@{
        Repo = $name; Files = $files; IdxMs = $idxMs; IdxPeakMB = $idxPeak; IdxKilled = $idxKilled; IdxErr = $idxErr
        Warns = $warns; Sidecar = $sidecar; Sym = $sym
        RefCs = $refCs; RefCpp = $refCpp; RefLex = $refLex; Parsed = $parsed; Candidates = $cand
        RefMs = $refMs; RefPeakMB = $refPeak; RefKilled = $refKilled; SearchMs = $searchMs
    }
}

# --- consolidated table ---
Write-Host ""
$fmt = "{0,-20} {1,8} {2,8} {3,9} {4,6} {5,10} {6,8} {7,9}"
Write-Host ($fmt -f 'repo', 'files', 'idx ms', 'idxPeak', 'warns', 'refs', 'refs ms', 'refPeak')
Write-Host ('-' * 88)
foreach ($r in $rows) {
    $refTot = if ($null -ne $r.RefCs) { "$($r.RefCs)/$($r.RefCpp)/$($r.RefLex)" } else { '-' }
    $flag = ''
    if ($r.IdxKilled -or $r.RefKilled) { $flag = ' !KILLED' }
    elseif ($r.IdxErr) { $flag = " !$($r.IdxErr)" }
    Write-Host (($fmt -f $r.Repo, $r.Files, [int]$r.IdxMs, "$([int]$r.IdxPeakMB)MB", $r.Warns, $refTot, [int]$r.RefMs, "$([int]$r.RefPeakMB)MB") + $flag)
}
Write-Host ""
Write-Host "refs column = C#/C++/lexical reference counts for an auto-picked symbol per repo." -ForegroundColor DarkGray
$maxIdxPeak = ($rows | Measure-Object -Property IdxPeakMB -Maximum).Maximum
$maxRefPeak = ($rows | Where-Object { $null -ne $_.RefPeakMB } | Measure-Object -Property RefPeakMB -Maximum).Maximum
$killed = @($rows | Where-Object { $_.IdxKilled -or $_.RefKilled }).Count
$errs = @($rows | Where-Object { $_.IdxErr }).Count
Write-Host "peaks: index max $([int]$maxIdxPeak)MB, refs max $([int]$maxRefPeak)MB | killed: $killed | index errors: $errs" -ForegroundColor Green

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $OutDir "sweep-$stamp.json"
$rows | ConvertTo-Json -Depth 6 | Out-File -FilePath $jsonPath -Encoding utf8
Write-Host "json: $jsonPath" -ForegroundColor DarkGray
