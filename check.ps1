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
                 * a broad C/C++ find_references (CodeSpawner broad-token, ~1 GB) is right against the
                   manifest, peaks under 768 MB, and stays under 2x the index time of the same corpus.
                 * a 10-edit CodeSpawner churn through `update` matches the composed ground truth.
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
  ./check.ps1 -Big         # unit + large-file scenarios (the release gate)
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

# Point git at the tracked hooks dir so .githooks/pre-push runs ./check.ps1 (build + xUnit) before every push;
# the release workflow runs the full -Big gate on GitHub for every push to main.
if ($InstallHook) {
    Push-Location $root
    try { git config core.hooksPath .githooks }
    finally { Pop-Location }
    Write-Host "Installed: git will run '.githooks/pre-push' (=> ./check.ps1) before each push." -ForegroundColor Green
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
    $null = $p.Handle  # cache the handle now: without it PS 5.1 reports ExitCode as $null after exit
    $exited = $p.WaitForExit($TimeoutSec * 1000)
    if (-not $exited) { try { $p.Kill() } catch {}; return @{ Out=""; Err="TIMEOUT"; Code=-1; TimedOut=$true } }
    $p.WaitForExit() # ensure redirected output is fully flushed to the files before we read them
    $res = @{ Out = ([string](Get-Content $o -Raw)); Err = ([string](Get-Content $e -Raw)); Code = $p.ExitCode; TimedOut=$false }
    Remove-Item $o, $e -Force
    return $res
}

# Invoke-Cli plus wall time and the process's peak working set. PeakWorkingSet64 is the OS's running maximum, so
# sampling it every 50 ms until exit misses at most the last 50 ms of growth.
function Invoke-CliMeasured([string[]]$CliArgs, [int]$TimeoutSec = 600) {
    $o = (New-TemporaryFile).FullName; $e = (New-TemporaryFile).FullName
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $cli -ArgumentList $CliArgs -NoNewWindow -PassThru `
                       -RedirectStandardOutput $o -RedirectStandardError $e
    $null = $p.Handle
    $peak = 0
    while (-not $p.HasExited) {
        try { $p.Refresh(); $peak = [math]::Max($peak, $p.PeakWorkingSet64) } catch {}
        if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) { try { $p.Kill() } catch {}; break }
        Start-Sleep -Milliseconds 50
    }
    $p.WaitForExit(); $sw.Stop()
    $res = @{ Out = ([string](Get-Content $o -Raw)); Err = ([string](Get-Content $e -Raw)); Code = $p.ExitCode
              Ms = [long]$sw.ElapsedMilliseconds; PeakBytes = [long]$peak }
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

    # C/C++ find_references is a NAME search: every whole-word use in code, never a comment, a string literal or the
    # definition itself, and it works whether or not the headers exist (no compiler involved).
    Section "C/C++ find_references: name matches in code"
    $refDir = Join-Path $root ".corpus/_refscheck"
    if (Test-Path $refDir) { Remove-Item $refDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $refDir | Out-Null
    Set-Content (Join-Path $refDir "shared.h") "int foo(int);" -Encoding utf8
    Set-Content (Join-Path $refDir "foo.c")    "#include `"shared.h`"`nint foo(int x){ return x + 1; }" -Encoding utf8
    0..2 | ForEach-Object { Set-Content (Join-Path $refDir "use$_.c") "#include `"shared.h`"`nint u$_(void){ return foo($_); } /* foo */ const char *s$_ = `"foo`";" -Encoding utf8 }
    # widget_reset is only declared in a MISSING header: a name search still finds every call.
    0..2 | ForEach-Object { Set-Content (Join-Path $refDir "mod$_.c") "#include `"hwdefs_missing.h`"`nint m$_(void){ return widget_reset($_); }" -Encoding utf8 }
    Invoke-Cli @("index", $refDir) | Out-Null

    $rFoo = Invoke-Cli @("refs", $refDir, "foo")
    Check "C/C++ calls are found by name" (([regex]::Matches($rFoo.Out, '(?m)^use\d\.c:2:')).Count -eq 3)
    Check "comments, strings and the definition are not references" ($rFoo.Out -notmatch '(?m)^foo\.c:2:' -and ([regex]::Matches($rFoo.Out, '(?m)^use\d\.c:')).Count -eq 3)
    Check "the answer says C/C++ is matched by name" ($rFoo.Out -match "matched by NAME")

    $rW = Invoke-Cli @("refs", $refDir, "widget_reset")
    Check "calls through a missing header are still found" (([regex]::Matches($rW.Out, '(?m)^mod\d\.c:2:')).Count -eq 3)
    Remove-Item $refDir -Recurse -Force -ErrorAction SilentlyContinue

    # A broad C/C++ find_references once took a machine to 9-11 GB and OOM. The name search has to stay bounded:
    # a CodeSpawner broad-token corpus (~1 GB: 200 files of 2-8 MB, one hot name in every one) is the worst shape
    # for it. Checks the answer against the manifest's ground truth, the process's peak memory, and its time
    # against indexing the same bytes on the same machine (a ratio, so the gate holds on any runner; a field build
    # once made refs 2.5x slower with every other check passing). Measured 2026-10-07 on 12 threads: index 5.7 s
    # 608 MB; refs 14 s 250 MB before the read-once fix, 4.6-6.3 s 350 MB after (0.8-1.1x). The 2x limit fails if
    # the filter goes back to re-reading each hit file (2.5-2.8x).
    Section "broad C/C++ find_references: right answer, bounded memory, no latency regression"
    $spawner = Join-Path $root "tools/codespawner/codespawner.exe"
    $broad = Join-Path $root ".corpus/_broadcheck"
    & $spawner gen --out $broad --preset broad-token --giant-headers 0 --force | Out-Null
    $bm = Get-Content "$broad-manifest.json" -Raw | ConvertFrom-Json
    Check "broad-token corpus generated" ($LASTEXITCODE -eq 0 -and @($bm.symbols.broad_hot.refs).Count -ge 100)

    $bIdx = Invoke-CliMeasured @("index", $broad)
    Check "broad-token corpus indexes" ($bIdx.Code -eq 0)
    $bRefs = Invoke-CliMeasured @("refs", $broad, "broad_hot")
    $sites = @{}
    foreach ($ln in ($bRefs.Out -split "`r?`n")) {
        $mm = [regex]::Match($ln, '^(?<p>.+?):(?<l>\d+):\d+:(?<t>.*)$')
        if ($mm.Success) { $sites[($mm.Groups['p'].Value -replace '\\', '/') + ":" + $mm.Groups['l'].Value] = $mm.Groups['t'].Value }
    }
    $missing = @($bm.symbols.broad_hot.refs | Where-Object { -not $sites.ContainsKey($_) })
    # Beyond the manifest's call sites, a name search may only add each carrier's prototype "int broad_hot(int x);".
    $odd = @($sites.Keys | Where-Object { ($bm.symbols.broad_hot.refs -notcontains $_) -and ($sites[$_] -notmatch '^\s*int broad_hot\(int x\);') })
    Check "refs finds every call site ($(@($bm.symbols.broad_hot.refs).Count)), nothing unexpected" ($bRefs.Code -eq 0 -and $missing.Count -eq 0 -and $odd.Count -eq 0)
    Check "refs leaves out the definition" (-not $sites.ContainsKey($bm.symbols.broad_hot.def))
    $peakMb = [math]::Round($bRefs.PeakBytes / 1MB)
    Check "refs peak memory ${peakMb} MB stays under 768 MB" ($bRefs.PeakBytes -gt 0 -and $bRefs.PeakBytes -lt 768MB)
    $ratio = [math]::Round($bRefs.Ms / [math]::Max(1, $bIdx.Ms), 2)
    Check "refs time $($bRefs.Ms) ms is under 2x the index time $($bIdx.Ms) ms (now $ratio x)" ($ratio -le 2)

    # Long churn through `codecompass update` against CodeSpawner's composed ground truth (removes, line shifts,
    # adds, and grow/shrink across the sidecar cutoff so sidecars are created and orphan-cleaned). 10 edits cycle
    # every edit type twice; that needs 2 shrink seeds (with 1, mutate crashes on the second Shrink).
    Section "churn: incremental update matches composed ground truth (10 edits)"
    $churn = Join-Path $root ".corpus/_churncheck"
    Remove-Item "$churn-delta*.json" -Force -ErrorAction SilentlyContinue
    & $spawner gen --out $churn --giant-headers 0 --shrink-seeds 2 --force | Out-Null
    Check "churn corpus generated" ($LASTEXITCODE -eq 0 -and (Test-Path "$churn-manifest.json"))
    & (Join-Path $root "verify-churn-codecompass.ps1") -Corpus $churn -Edits 10 -Cli $cli | Out-Host
    Check "churn oracle passes (10 steps)" ($LASTEXITCODE -eq 0)
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
