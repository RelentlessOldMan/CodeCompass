<#
.SYNOPSIS
  One command to verify CodeCompass. Fast unit tests by default; add -Big to also run the heavy
  large-file / generated-corpus scenarios (with pass/fail assertions), and -Fetch to also pull the
  pinned real repos and run the correctness bench. Intended as the "before I push from this machine"
  gate, and as the "grab the code and reproduce everything" entry point.

.DESCRIPTION
  Tiers (each includes the ones above it):
    (default)  dotnet test        - the xUnit suite (~165 tests, ~10s). Fast; run this constantly.
    -Big       + large-file cases - generates synthetic corpora (gitignored) and asserts:
                 * a ~SizeGB register-map header indexes (streaming) and a marker near EOF is found
                   at the right line via the positional index; a broad query reports truncation.
                 * the nested-template "pathological" files index without hanging at the default cap.
    -Fetch     + real-repo bench  - fetch-corpus + `bench verify` (needs network; large).

  Exits non-zero if anything fails, so it's usable as a pre-push check:  ./check.ps1 -Big

.PARAMETER Big     Run the heavy large-file / generated-corpus scenarios.
.PARAMETER Fetch   Also fetch pinned real repos and run the correctness bench (network, large).
.PARAMETER SizeGB  Size of the big register-map header for -Big (default 0.3; crosses the 128 MB
                   streaming threshold quickly). Use 2 for the full multi-GB run.

.EXAMPLE
  ./check.ps1              # fast unit tests
  ./check.ps1 -Big         # unit + large-file scenarios (pre-push)
  ./check.ps1 -Big -SizeGB 2
#>
param(
    [switch]$Big,
    [switch]$Fetch,
    [double]$SizeGB = 0.3
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "  PASS  $t" -ForegroundColor Green }
function Bad($t)  { Write-Host "  FAIL  $t" -ForegroundColor Red; $failures.Add($t) }
function Check($name, [bool]$cond) { if ($cond) { Ok $name } else { Bad $name } }

# Run the built CLI capturing stdout/stderr/exit without PowerShell's native-stderr pitfalls, with a
# timeout so a hang is a failure rather than a wedge.
$cli = Join-Path $root "src/CodeCompass.Cli/bin/Release/net8.0/CodeCompass.Cli.exe"
function Invoke-Cli([string[]]$CliArgs, [int]$TimeoutSec = 600) {
    $o = (New-TemporaryFile).FullName; $e = (New-TemporaryFile).FullName
    $p = Start-Process -FilePath $cli -ArgumentList $CliArgs -NoNewWindow -PassThru `
                       -RedirectStandardOutput $o -RedirectStandardError $e
    $exited = $p.WaitForExit($TimeoutSec * 1000)
    if (-not $exited) { try { $p.Kill() } catch {}; return @{ Out=""; Err="TIMEOUT"; Code=-1; TimedOut=$true } }
    $p.WaitForExit() # ensure redirected output is fully flushed to the files before we read them
    $res = @{ Out = ([string](Get-Content $o -Raw)); Err = ([string](Get-Content $e -Raw)); Code = $p.ExitCode; TimedOut=$false }
    Remove-Item $o, $e -Force
    return $res
}

# ---- Tier 1: fast unit tests --------------------------------------------------------------------
Section "unit tests (dotnet test)"
dotnet test -c Release --nologo
Check "xUnit suite" ($LASTEXITCODE -eq 0)

# ---- Tier 2: large-file / generated-corpus scenarios --------------------------------------------
if ($Big -or $Fetch) {
    Section "build (Release)"
    dotnet build -c Release --nologo | Out-Null
    Check "build" ($LASTEXITCODE -eq 0 -and (Test-Path $cli))
}

if ($Big -and (Test-Path $cli)) {
    Section "big file: streaming index + positional search + truncation"
    & (Join-Path $root "make-bigfile-corpus.ps1") -SizeGB $SizeGB | Out-Null
    $bigDir = Join-Path $root ".corpus/_bigfile"
    Start-Sleep -Seconds 2  # let a freshly-written large file settle (AV) before the first walk

    $idx = Invoke-Cli @("index", $bigDir)
    Check "big file indexes (no hang)" (-not $idx.TimedOut)

    # The real correctness proof: the fresh index can find a marker near EOF (and at the right line).
    $end = Invoke-Cli @("search", $bigDir, "ZZ_UNIQUE_MARKER_END_e5d0")
    Check "marker near EOF found via positional index" ($end.Out -match "chipreg_bank\.h:\d+:\d+:.*ZZ_UNIQUE_MARKER_END_e5d0")

    $hey = Invoke-Cli @("search", $bigDir, "HEY")
    Check "broad query reports truncation" ($hey.Err -match "MORE EXIST")

    Section "pathological: nested-template files index without hanging (default symbol cap)"
    & (Join-Path $root "make-pathological-corpus.ps1") -Count 8 -SizeMB 3 | Out-Null
    $nt = Join-Path $root ".corpus/_pathological/slow_nested_templates"
    $pat = Invoke-Cli @("index", $nt) 120   # default cap skips >1MB symbol extraction -> must be fast
    Check "pathological indexes without hanging" (-not $pat.TimedOut)
}

# ---- Tier 3: real-repo correctness bench --------------------------------------------------------
if ($Fetch) {
    Section "real repos: fetch + correctness bench"
    & (Join-Path $root "fetch-corpus.ps1") small | Out-Null
    Check "fetch small corpus" ($LASTEXITCODE -eq 0)
    # bench verify is the lexical oracle (trigram search vs brute force) on the fetched repos.
    dotnet run -c Release --project (Join-Path $root "src/CodeCompass.Bench") -- verify small
    Check "bench verify (search oracle)" ($LASTEXITCODE -eq 0)
}

# ---- Summary ------------------------------------------------------------------------------------
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
}
Write-Host ("FAILED: " + ($failures -join ", ")) -ForegroundColor Red
exit 1
