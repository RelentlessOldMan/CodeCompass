<#
.SYNOPSIS
  One command to verify CodeCompass. Fast unit tests by default; add -Big to also run the heavy
  large-file / generated-corpus scenarios (with pass/fail assertions), and -Fetch to also pull the
  pinned real repos and run the correctness bench. Intended as the "before I push from this machine"
  gate, and as the "grab the code and reproduce everything" entry point.

.DESCRIPTION
  Tiers (each includes the ones above it):
    (default)  dotnet test        - the xUnit suite (~170 tests, ~10s). Fast; run this constantly.
    -Big       + large-file cases - generates synthetic corpora (gitignored) and asserts:
                 * a ~SizeGB register-map header indexes (streaming) and a marker near EOF is found
                   at the right line via the positional index; a broad query reports truncation.
                 * the SAME big file, with the network path FAKED ON (CODECOMPASS_FORCE_NETWORK=1),
                   still indexes (pre-scan skipped) and the positional search still finds the marker.
                 * the nested-template "pathological" files index without hanging at the default cap.
    -Fetch     + real-repo bench  - fetch-corpus + `bench verify` (needs network; large).

  Exits non-zero if anything fails, so it's usable as a pre-push check:  ./check.ps1 -Big

  Manual (not part of the tiers, needs a real share you provide):
    -Network <path>  Generate a big-file corpus ON that network path, then index + positional-search
                     it over the wire - the real-SMB counterpart to the faked-network -Big check.
                     Any WRITABLE network location works (e.g. \\host\share\scratch); the script
                     creates its own 'codecompass-nettest' subdir and removes it afterward. No
                     pre-existing repo or manual setup needed. Uses -SizeGB for the file size.

.PARAMETER Big      Run the heavy large-file / generated-corpus scenarios.
.PARAMETER Fetch    Also fetch pinned real repos and run the correctness bench (network, large).
.PARAMETER SizeGB   Size of the big register-map header for -Big / -Network (default 0.3; crosses the
                    128 MB streaming threshold quickly). Use 2 for the full multi-GB run.
.PARAMETER Network  A writable network path to run the real-share smoke test against (see above).

.EXAMPLE
  ./check.ps1              # fast unit tests
  ./check.ps1 -Big         # unit + large-file scenarios (pre-push)
  ./check.ps1 -Big -SizeGB 2
  ./check.ps1 -Network \\myserver\share\scratch   # real-share smoke test (run by hand)
#>
param(
    [switch]$Big,
    [switch]$Fetch,
    [double]$SizeGB = 0.3,
    [string]$Network,
    [switch]$InstallHook
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]
Set-Location $root   # dotnet restore/build/test below resolve the solution from the current directory

# Point git at the tracked hooks dir so .githooks/pre-push runs ./check.ps1 -Big before every push.
if ($InstallHook) {
    Push-Location $root
    try { git config core.hooksPath .githooks }
    finally { Pop-Location }
    Write-Host "Installed: git will run '.githooks/pre-push' (=> ./check.ps1 -Big) before each push." -ForegroundColor Green
    Write-Host "Bypass a single push with:  git push --no-verify"
    exit 0
}

function Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "  PASS  $t" -ForegroundColor Green }
function Bad($t)  { Write-Host "  FAIL  $t" -ForegroundColor Red; $failures.Add($t) }
function Check($name, [bool]$cond) { if ($cond) { Ok $name } else { Bad $name } }

# Run the built CLI capturing stdout/stderr/exit without PowerShell's native-stderr pitfalls, with a
# timeout so a hang is a failure rather than a wedge.
$cli = Join-Path $root "src/CodeCompass.Cli/bin/Release/net10.0/CodeCompass.Cli.exe"
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

# ---- Tier 0: dependency lock (drift guard) ------------------------------------------------------
# Restore in LOCKED mode: fails if the resolved package graph no longer matches the committed
# packages.lock.json. This is the guard that would have caught the silent transitive drift that broke
# the MCP server (a floating System.Text.Json floated from a net8 to a net10 version between releases).
# A legitimate dependency change must regenerate the lockfile (dotnet restore --force-evaluate) - which
# shows up as a reviewable diff - before this passes.
Section "dependency lock (restore --locked-mode)"
# Locked-mode restore fails if the resolved package graph no longer matches the committed
# packages.lock.json - the guard for silent transitive drift (a floating System.Text.Json floated from a
# net8 to a net10 version between releases and broke the server). The lockfiles are RID-less and pin our
# DEPENDENCY versions, which is what we protect; the win-x64 PUBLISH additionally pulls runtime-host
# packages (discarded post-publish in make-release) but still uses these locked dependency versions.
dotnet restore --locked-mode --nologo
Check "package graph matches packages.lock.json" ($LASTEXITCODE -eq 0)
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Dependencies drifted from the lockfile. If intentional, run: dotnet restore --force-evaluate, then review + commit the packages.lock.json diff." -ForegroundColor Yellow
}

# ---- Tier 1: build + unit tests -----------------------------------------------------------------
# Build the WHOLE solution first: the test project doesn't reference the CLI, but its end-to-end tests run the
# built CodeCompass.Cli.exe as a subprocess - built here, so they always exercise THIS commit's binary (TestCli
# fails them loudly on a missing or stale exe rather than letting them pass vacuously).
Section "build (Release)"
dotnet build -c Release --nologo | Out-Null
Check "build" ($LASTEXITCODE -eq 0 -and (Test-Path $cli))

Section "unit tests (dotnet test)"
dotnet test -c Release --nologo --no-build
Check "xUnit suite" ($LASTEXITCODE -eq 0)

if ($Big -and (Test-Path $cli)) {
    Section "big file: streaming index + positional search + truncation"
    & (Join-Path $root "make-bigfile-corpus.ps1") -SizeGB $SizeGB | Out-Null
    $bigDir = Join-Path $root ".corpus/_bigfile"
    Check "big-file corpus generated" (Test-Path (Join-Path $bigDir "chipreg_bank.h"))
    Start-Sleep -Seconds 2  # let a freshly-written large file settle (AV) before the first walk

    # "No hang" alone would pass a CLI that fails instantly - require a clean exit too.
    $idx = Invoke-Cli @("index", $bigDir)
    Check "big file indexes (no hang, exit 0)" (-not $idx.TimedOut -and $idx.Code -eq 0)

    # The real correctness proof: the fresh index can find a marker near EOF (and at the right line).
    $end = Invoke-Cli @("search", $bigDir, "ZZ_UNIQUE_MARKER_END_e5d0")
    Check "marker near EOF found via positional index" ($end.Out -match "chipreg_bank\.h:\d+:\d+:.*ZZ_UNIQUE_MARKER_END_e5d0")

    $hey = Invoke-Cli @("search", $bigDir, "HEY")
    Check "broad query reports truncation" ($hey.Err -match "MORE EXIST")

    Section "big file with the network path FAKED ON (CODECOMPASS_FORCE_NETWORK=1)"
    # Exercise the network branch on local disk: index must skip the pre-scan (no %/ETA round-trips)
    # and still complete, and the positional search must still find the EOF marker (reads only the
    # candidate block - the whole point over a share). Real-SMB counterpart is ./check.ps1 -Network.
    $env:CODECOMPASS_FORCE_NETWORK = "1"
    try {
        $nidx = Invoke-Cli @("index", $bigDir)
        Check "network-mode big file indexes (no hang, exit 0)" (-not $nidx.TimedOut -and $nidx.Code -eq 0)
        Check "network-mode index skips the pre-scan" ($nidx.Err -match "skipping the pre-scan")
        $nend = Invoke-Cli @("search", $bigDir, "ZZ_UNIQUE_MARKER_END_e5d0")
        Check "network-mode positional search finds EOF marker" ($nend.Out -match "chipreg_bank\.h:\d+:\d+:.*ZZ_UNIQUE_MARKER_END_e5d0")
    }
    finally { Remove-Item Env:\CODECOMPASS_FORCE_NETWORK -ErrorAction SilentlyContinue }

    Section "pathological: nested-template files index without hanging (default symbol cap)"
    & (Join-Path $root "make-pathological-corpus.ps1") -Count 8 -SizeMB 3 | Out-Null
    $nt = Join-Path $root ".corpus/_pathological/slow_nested_templates"
    Check "pathological corpus generated" ((Test-Path $nt) -and @(Get-ChildItem $nt -File).Count -gt 0)
    $pat = Invoke-Cli @("index", $nt) 120   # default cap skips >1MB symbol extraction -> must be fast
    Check "pathological indexes without hanging (exit 0)" (-not $pat.TimedOut -and $pat.Code -eq 0)
    $patQ = Invoke-Cli @("symbols", $nt, "a")
    Check "pathological index is queryable" ($patQ.Code -eq 0)

    Section "diagnostics: doctor + report (CLI wiring)"
    $doc = Invoke-Cli @("doctor", $bigDir)
    Check "doctor runs health checks" ($doc.Out -match "== health ==" -and $doc.Out -match "index loads cleanly")
    $reportZip = Join-Path $env:TEMP ("cc-report-check-{0}.zip" -f $PID)
    $rep = Invoke-Cli @("report", $bigDir, "--out", $reportZip)
    $zipOk = (Test-Path $reportZip)
    # The bundle must contain diagnostics but must NOT contain the source header (never ship source).
    $noSource = $true
    if ($zipOk) {
        try {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zf = [System.IO.Compression.ZipFile]::OpenRead($reportZip)
            try {
                $names = $zf.Entries.FullName
                $noSource = ($names -contains "diagnostics.txt") -and (-not ($names -match "chipreg_bank\.h"))
            } finally { $zf.Dispose() }
        } catch { $noSource = $false }
        Remove-Item $reportZip -Force -ErrorAction SilentlyContinue
    }
    Check "report writes a bundle with diagnostics and no source" ($zipOk -and $noSource)

    Section "linked roots: link add size gate (defer large fresh tree, index small one)"
    # Guards the `link add` decision on a FRESH, unindexed external tree: a root over the auto-index cap must
    # DEFER (report the threshold, build nothing) - not misreport a size or index inline; a small one indexes.
    $linkBase = Join-Path $root ".corpus/_linktest"
    Remove-Item $linkBase -Recurse -Force -ErrorAction SilentlyContinue
    $proj  = Join-Path $linkBase "project"
    $bigL  = Join-Path $linkBase "biglink"
    $smallL = Join-Path $linkBase "smalllink"
    New-Item -ItemType Directory -Force -Path $proj, $bigL, $smallL | Out-Null
    Set-Content (Join-Path $proj  "app.c")  "int app(void){return 0;}" -Encoding utf8
    Set-Content (Join-Path $bigL  "code.c") "int f(void){return 0;}"   -Encoding utf8
    Set-Content (Join-Path $bigL ".codecompass.json") '{ "maxAutoMb": 0 }' -Encoding utf8  # zero cap -> over limit
    Set-Content (Join-Path $smallL "util.c") "int util(void){return 1;}" -Encoding utf8
    # Defensive: drop any stale links from a prior run (project path is stable across runs).
    Invoke-Cli @("link", "remove", $bigL,  $proj, "--keep") | Out-Null
    Invoke-Cli @("link", "remove", $smallL, $proj, "--keep") | Out-Null

    $laBig = Invoke-Cli @("link", "add", $bigL, $proj)
    Check "link add defers an over-cap fresh tree (reports the limit)" ($laBig.Out -match "auto-index limit")
    $docL = Invoke-Cli @("doctor", $proj)
    Check "deferred link is present but NOT indexed" ($docL.Out -match "indexed: NO")

    $laSmall = Invoke-Cli @("link", "add", $smallL, $proj)
    Check "link add indexes a small fresh tree inline" ($laSmall.Out -match "indexed \d")
    Invoke-Cli @("link", "remove", $bigL,  $proj, "--keep") | Out-Null
    Invoke-Cli @("link", "remove", $smallL, $proj, "--purge") | Out-Null

    # C/C++ find_references correctness - the path where every field bug this cycle lived, previously NOT
    # exercised by the gate at all. Two crafted cases:
    #   (1) resolvable symbol       => semantic refs found, NO lexical backfill (covered files, no double-count)
    #   (2) unresolved-include symbol => the "false zero" case: 0 semantic but lexical BACKFILLS + discloses
    Section "C/C++ find_references: complete coverage vs unresolved-include fallback"
    $refDir = Join-Path $root ".corpus/_refscheck"
    if (Test-Path $refDir) { Remove-Item $refDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $refDir | Out-Null
    Set-Content (Join-Path $refDir "shared.h") "int foo(int);" -Encoding utf8
    Set-Content (Join-Path $refDir "foo.c")    "#include `"shared.h`"`nint foo(int x){ return x + 1; }" -Encoding utf8
    0..2 | ForEach-Object { Set-Content (Join-Path $refDir "use$_.c") "#include `"shared.h`"`nint u$_(void){ return foo($_); }" -Encoding utf8 }
    # widget_reset is only declared in a MISSING header -> parses-with-errors, 0 semantic, real call sites.
    0..2 | ForEach-Object { Set-Content (Join-Path $refDir "mod$_.c") "#include `"hwdefs_missing.h`"`nint m$_(void){ return widget_reset($_); }" -Encoding utf8 }
    Invoke-Cli @("index", $refDir) | Out-Null

    $rFoo = Invoke-Cli @("refs", $refDir, "foo")
    Check "resolvable symbol resolves semantically" ($rFoo.Err -match "\+\s+[1-9]\d*\s+C/C\+\+ semantic")
    Check "resolvable symbol does NOT double-count via lexical" ($rFoo.Err -match "semantic\s+\+\s+0\s+lexical")

    $rW = Invoke-Cli @("refs", $refDir, "widget_reset")
    Check "unresolved-include symbol discloses incomplete coverage" ($rW.Out -match "unresolved #include")
    Check "unresolved-include symbol backfills lexical (no false zero)" ($rW.Err -match "\+\s+[1-9]\d*\s+lexical reference")
    Remove-Item $refDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- Tier 3: real-repo correctness bench --------------------------------------------------------
if ($Fetch) {
    Section "real repos: fetch + correctness bench"
    & (Join-Path $root "fetch-corpus.ps1") small | Out-Null
    Check "fetch small corpus" ($LASTEXITCODE -eq 0)
    # bench verify is the lexical oracle (trigram search vs brute force) over every repo in the small
    # tier - now including t32scripts, so .cmm text search is checked against real PRACTICE, not synthetic.
    dotnet run -c Release --project (Join-Path $root "src/CodeCompass.Bench") -- verify small
    Check "bench verify (search oracle)" ($LASTEXITCODE -eq 0)
    # Symbol smoke on the real TRACE32 PRACTICE repo: proves the vendored .cmm grammar extracts symbols
    # from real board scripts (a zero here = grammar not loading / node-name drift).
    dotnet run -c Release --project (Join-Path $root "src/CodeCompass.Bench") -- symbols t32scripts
    Check "bench symbols (.cmm PRACTICE grammar)" ($LASTEXITCODE -eq 0)
}

# ---- Manual: real network share -----------------------------------------------------------------
# The real-SMB counterpart to the faked-network -Big check. Self-contained: generates its own corpus
# on the share you provide and removes it afterward - any writable network location works, no
# pre-existing repo needed. Indexing reads the file over the wire once; the positional search then
# reads only the candidate block, which is the behaviour this proves is cheap over a network.
if ($Network -and (Test-Path $cli)) {
    Section "real network share: $Network"
    if (-not (Test-Path $Network)) {
        Bad "network path is reachable ($Network not found)"
    }
    else {
        $netDir = Join-Path $Network "codecompass-nettest"
        try {
            & (Join-Path $root "make-bigfile-corpus.ps1") -SizeGB $SizeGB -OutDir $netDir | Out-Null
            Check "corpus written to the share" (Test-Path (Join-Path $netDir "chipreg_bank.h"))
            Start-Sleep -Seconds 2  # let the freshly-written file settle before the first walk

            $nidx = Invoke-Cli @("index", $netDir)
            Check "share big file indexes over the wire (no hang, exit 0)" (-not $nidx.TimedOut -and $nidx.Code -eq 0)
            Check "share index skips the pre-scan" ($nidx.Err -match "skipping the pre-scan")

            $nend = Invoke-Cli @("search", $netDir, "ZZ_UNIQUE_MARKER_END_e5d0")
            Check "share positional search finds EOF marker" ($nend.Out -match "chipreg_bank\.h:\d+:\d+:.*ZZ_UNIQUE_MARKER_END_e5d0")

            $nhey = Invoke-Cli @("search", $netDir, "HEY")
            Check "share broad query reports truncation" ($nhey.Err -match "MORE EXIST")
        }
        finally {
            # Clean up the corpus we created on the share (best-effort).
            if (Test-Path $netDir) { Remove-Item $netDir -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

# ---- Summary ------------------------------------------------------------------------------------
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
}
Write-Host ("FAILED: " + ($failures -join ", ")) -ForegroundColor Red
exit 1
