<#
.SYNOPSIS
  Build a one-download release of the CodeCompass plugin: the self-contained win-x64 binaries + MCP
  manifest + hooks + docs, zipped and (optionally) published to GitHub Releases so others can install
  without the .NET SDK or a source build.

.DESCRIPTION
  Runs build-plugin.ps1 (publishes plugin/bin), zips the whole plugin/ folder to
  codecompass-plugin-<version>-win-x64.zip (gitignored), and prints install steps. Every push to main runs this
  automatically (.github/workflows/release.yml, `-Publish` on a GitHub Windows runner); run it by hand for a local
  zip, or to publish without pushing new commits.

  -Publish uploads the zip to a GitHub Release tagged v<version> via the `gh` CLI (must be installed and
  authenticated: `gh auth status`). Without -Publish it only builds the local zip.

.PARAMETER Publish     Also create/update the GitHub Release v<version> and upload the zip (needs `gh`).
.PARAMETER SkipTests   Skip the pre-publish test gate (check.ps1 -Big). Use only if you JUST ran it; the
                       push's pre-push hook (if installed) is then the only backstop.

.EXAMPLE
  pwsh ./make-release.ps1              # build the local release zip
  pwsh ./make-release.ps1 -Publish     # test-gate + build + publish to GitHub Releases
#>
param([switch]$Publish, [switch]$SkipTests)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# build-plugin stamps the release version into these TRACKED manifests; the stamp is build output and is never
# committed (the committed value is the 0.0.0-dev sentinel). Restore them - but only when the version is the ONLY
# difference from HEAD, so a genuine uncommitted manifest edit is never thrown away.
$stampedManifests = @("plugin/.claude-plugin/plugin.json", "plugin/plugin.json")
function Restore-StampedManifests {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        foreach ($m in $stampedManifests) {
            $path = Join-Path $root $m
            if (-not (Test-Path $path)) { continue }
            git -C $root diff --quiet -- $m
            if ($LASTEXITCODE -eq 0) { continue }                       # unchanged
            $head = git -C $root show "HEAD:$m" 2>$null
            if ($LASTEXITCODE -ne 0) { continue }
            $a = ($head -join "`n") | ConvertFrom-Json
            $b = Get-Content $path -Raw | ConvertFrom-Json
            $a.version = "x"; $b.version = "x"
            if (($a | ConvertTo-Json -Depth 20 -Compress) -eq ($b | ConvertTo-Json -Depth 20 -Compress)) {
                git -C $root checkout -- $m
            } else {
                Write-Warning "$m has uncommitted edits beyond the version stamp - leaving it for you to commit or revert."
            }
        }
    }
    finally { $ErrorActionPreference = $prev }
}

# 0) Pre-publish gate + source sync. Done BEFORE the build on purpose: build-plugin stamps the version
#    into plugin.json (a tracked file), which would dirty the tree - so validate cleanliness and push the
#    COMMIT here, while the tree is still pristine. Pushing the commit (not the working tree) means the
#    later build-time manifest stamp is irrelevant, and the tag lands on this exact, tested, pushed commit.
$sha = $null
if ($Publish) {
    # a) A published release MUST be tested - don't rely on the pre-push hook being installed here. Run the
    #    full gate (unit + big-file/network-sim/pathological/diagnostics); we push --no-verify so it doesn't
    #    run again via the hook. -SkipTests opts out (with a warning; the hook, if installed, still guards).
    if ($SkipTests) {
        Write-Warning "SkipTests: NOT running check.ps1 -Big. Only the pre-push hook (if installed) will gate this release."
    }
    else {
        Write-Host "Running the release test gate (check.ps1 -Big) ..."
        & (Join-Path $root "check.ps1") -Big
        if ($LASTEXITCODE -ne 0) { throw "check.ps1 -Big failed - release aborted." }
    }

    # b) The source that built this release must be on origin/main, or a work checkout sees stale source
    #    and the tag drifts. Require a clean tree + clean fast-forward, then push HEAD -> main. (This bit
    #    us once; never again.)
    $sha = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "could not resolve HEAD" }

    Restore-StampedManifests   # a previous build's version stamp is not "uncommitted work"
    $dirty = git status --porcelain
    if ($dirty) { throw "working tree has uncommitted/untracked changes - commit them before releasing:`n$dirty" }

    # git fetch/push write their normal progress to STDERR even on success; under ErrorActionPreference=Stop
    # (PowerShell 5.1) that stderr is turned into a terminating NativeCommandError, aborting a successful
    # push. So run the native git calls under Continue and decide purely on $LASTEXITCODE (same guard the
    # gh block below uses).
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        Write-Host "Fetching origin to check main is fast-forwardable ..."
        git fetch origin --quiet
        if ($LASTEXITCODE -ne 0) { throw "git fetch origin failed" }

        # HEAD must be at or ahead of origin/main. If origin/main has commits this build doesn't, stop.
        git merge-base --is-ancestor origin/main HEAD
        if ($LASTEXITCODE -ne 0) { throw "origin/main has commits not in this build (diverged) - reconcile (git pull --rebase) before releasing." }

        # Skip the push entirely if origin/main is already at HEAD (the usual case: we push before releasing).
        # A no-op `git push` prints "Everything up-to-date" to STDERR, which surfaces as noisy NativeCommandError
        # output when the release is run under 2>&1. Nothing to push -> say so and move on.
        git merge-base --is-ancestor HEAD origin/main
        if ($LASTEXITCODE -eq 0) {
            Write-Host "origin/main already at $sha - nothing to push."
        } else {
            Write-Host "Pushing $sha -> origin/main ..."
            # --no-verify: we already ran check.ps1 -Big above, so skip the pre-push hook's redundant re-run.
            # (With -SkipTests we did NOT gate here - let the hook run, so keep verification on that push.)
            $pushArgs = @("push", "origin", "${sha}:refs/heads/main")
            if (-not $SkipTests) { $pushArgs = @("push", "--no-verify", "origin", "${sha}:refs/heads/main") }
            git @pushArgs
            if ($LASTEXITCODE -ne 0) { throw "git push to origin/main failed - resolve, then re-run." }
        }
    }
    finally { $ErrorActionPreference = $prevEap }
}

# 1) Build the plugin (self-contained binaries + stamped plugin.json version).
& (Join-Path $root "build-plugin.ps1")
if ($LASTEXITCODE -ne 0) { throw "build-plugin.ps1 failed" }

# The `-r win-x64` publish rewrites each packages.lock.json to add win-x64 runtime-host packages. Our
# committed lockfiles are deliberately RID-less (they pin our DEPENDENCY versions, which is the drift we
# guard; the publish still uses those locked versions). Discard that RID churn so the release tree stays
# clean and the committed lockfiles remain the single RID-less source of truth the gate checks.
# Under ErrorActionPreference=Stop (PowerShell 5.1) a stderr line from git would turn into a terminating error AFTER
# a successful build; and a failed restore must not leave lockfile churn to break the NEXT release's clean-tree check
# with a confusing message. Decide on the exit code and warn, never throw.
$prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
git checkout -- ':(glob)**/packages.lock.json' 2>$null
if ($LASTEXITCODE -ne 0) { Write-Warning "could not restore packages.lock.json after publish - run: git checkout -- '**/packages.lock.json'" }
$ErrorActionPreference = $prevEap

# 1a) Smoke the PUBLISHED MCP server exe: do a real MCP stdio handshake and require it to list its tools.
# This is the gap that let a broken self-contained server ship (1.0.140-1.0.145 crashed on startup because a
# net8 build couldn't carry the MCP SDK's .NET 10 base libraries) - the -Big gate only exercises the CLI and
# in-process tests, never the shipped server binary. Abort the release if the server can't start and answer.
Write-Host "Smoke-testing the published MCP server (initialize + tools/list) ..."
$mcpExe = Join-Path $root "plugin/bin/CodeCompass.Mcp.exe"
if (-not (Test-Path $mcpExe)) { throw "published MCP exe missing: $mcpExe" }
$psi = [System.Diagnostics.ProcessStartInfo]::new($mcpExe)
$psi.WorkingDirectory = $root
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
$mcpProc = [System.Diagnostics.Process]::Start($psi)
try {
    # Drain stderr from the start: the server logs to stderr at startup, and an undrained stderr pipe that fills
    # would block it before it ever answers - a spurious "server does not start" abort.
    $errTask = $mcpProc.StandardError.ReadToEndAsync()
    $mcpProc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"release-smoke","version":"1"}}}')
    $mcpProc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $mcpProc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
    $mcpProc.StandardInput.Flush()
    # Read replies until the tools/list answer (id 2) arrives, THEN close stdin - like a real client. Closing stdin
    # right after writing races the SDK: on EOF the session ends and replies not yet written are dropped (seen on
    # every build, old and new, once the box was under load), which aborted a release of a healthy server.
    $out = ""; $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $lineTask = $mcpProc.StandardOutput.ReadLineAsync()
        if (-not $lineTask.Wait([Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds))) { break }
        if ($null -eq $lineTask.Result) { break }   # server exited
        $out += $lineTask.Result + "`n"
        if ($lineTask.Result -match '"id"\s*:\s*2\b') { break }
    }
    $mcpProc.StandardInput.Close()             # EOF -> the stdio server exits
    if (-not $mcpProc.WaitForExit(30000)) { $mcpProc.Kill() }
    if ($out -notmatch '"tools"' -or $out -notmatch 'find_definition') {
        throw "MCP server smoke FAILED - no tools/list response. The published server does not start/answer. stderr:`n$($errTask.Result)"
    }
    Write-Host "  PASS  MCP server responds to initialize + tools/list (tools present)."
}
finally {
    # Fully terminate the smoke process AND wait for the OS to release its handle on CodeCompass.Mcp.exe
    # before we package plugin/bin - otherwise the zip below races the dying process and hits a file lock.
    try { if (-not $mcpProc.HasExited) { $mcpProc.Kill() } } catch {}
    try { $mcpProc.WaitForExit(5000) | Out-Null } catch {}
    try { $mcpProc.Dispose() } catch {}
}

# 1b) Both shipped exes MUST disable Tiered PGO (System.Runtime.TieredPGO:false) - on net10 it makes the
# C/C++ find_references hot path ~2.5x slower (1.0.153 field regression). The csproj <TieredPGO>false</TieredPGO>
# bakes it into runtimeconfig.json; guard the SHIPPED artifact so the fix can never silently fall out of a
# release (an allocation/-Big timing check can't see a JIT/PGO effect - this structural check can).
foreach ($rc in @("CodeCompass.Cli.runtimeconfig.json", "CodeCompass.Mcp.runtimeconfig.json")) {
    $rcPath = Join-Path $root "plugin/bin/$rc"
    if (-not (Test-Path $rcPath)) { throw "runtimeconfig missing: $rcPath" }
    $props = (Get-Content $rcPath -Raw | ConvertFrom-Json).runtimeOptions.configProperties
    if ($props.'System.Runtime.TieredPGO' -ne $false) {
        throw "$rc does NOT set System.Runtime.TieredPGO=false - the net10 C/C++ refs perf regression would ship. Add <TieredPGO>false</TieredPGO> to the csproj."
    }
}
Write-Host "  PASS  shipped exes disable Tiered PGO (net10 C/C++ refs perf guard)."

# 2) Version = the numeric version build-plugin stamped into the manifest (matches `codecompass version`).
$pjPath = Join-Path $root "plugin/.claude-plugin/plugin.json"
$version = (Get-Content $pjPath -Raw | ConvertFrom-Json).version
if (-not $version) { throw "could not read version from $pjPath" }

# Guard against a mislabeled release: the manifest version must match what the SHIPPED binary reports.
# (If stamping ever silently failed, this catches it before we tag/name the zip after a stale version.)
$cliExe = Join-Path $root "plugin/bin/CodeCompass.Cli.exe"
$cliVerRaw = (& $cliExe version)
if ($LASTEXITCODE -ne 0) { throw "could not run $cliExe to confirm the version" }
$cliVer = ($cliVerRaw -replace '^CodeCompass\s+', '') -replace '\+.*$', ''
if ($cliVer -ne $version) { throw "version mismatch: manifest '$version' vs binary '$cliVer' - build is inconsistent, aborting." }

# 3) Drop a top-level INSTALL.txt into the plugin folder so it's the first thing a zip installer sees.
#    (gitignored; regenerated each release.) It states the layout that trips people up: the unzipped
#    folder IS the plugin - there is no plugin\ subfolder to point at.
$installTxt = @"
CodeCompass plugin $version - install (Windows, no .NET needed)
==============================================================

This folder IS the plugin. It directly contains .claude-plugin\, bin\, and hooks\.
There is NO plugin\ subfolder to point at - use THIS folder.

NOTE: there is no "/plugin add <path>" command. Use one of these:

PERSISTENT (stays installed across sessions) - this folder is a self-marketplace:

    /plugin marketplace add "<full path to this folder>"
    /plugin install codecompass@codecompass

e.g.  /plugin marketplace add "C:\Tools\codecompass-plugin-$version-win-x64"
      /plugin install codecompass@codecompass

ONE SESSION ONLY (no install) - launch Claude Code with:

    claude --plugin-dir "<full path to this folder>"

Then run  /mcp  to confirm the "codecompass" server is connected.

--------------------------------------------------------------
CODEX (OpenAI) - this folder is also a Codex plugin (marketplace + MCP + skill):

    ./install-codecompass.ps1 -TargetHost Codex

That runs the native Codex plugin flow (registers the MCP server AND a steering skill).
Or do it by hand:

    codex plugin marketplace add "<full path to this folder>"
    codex plugin add codecompass@codecompass

The plugin resolves the server path automatically (nothing hardcoded, nothing copied).
The server serves Codex's working directory, so open Codex in your project root.
Verify with:  codex plugin list   and   codex mcp list

(Older Codex without "codex plugin"? The installer falls back to
 codex mcp add codecompass -- "<full path>\bin\CodeCompass.Mcp.exe" - server only, no skill.)

Docs: open CodeCompass.html in this folder, or see
https://github.com/RelentlessOldMan/CodeCompass
"@
Set-Content -Path (Join-Path $root "plugin/INSTALL.txt") -Value $installTxt -Encoding utf8

# 4) Zip the plugin folder's contents (so it extracts to a directory containing .claude-plugin).
$zip = Join-Path $root ("codecompass-plugin-{0}-win-x64.zip" -f $version)
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host "Zipping plugin -> $zip ..."
# The freshly-published exe can be briefly locked (AV scan / handle not yet released after the version
# probe). Let it settle and retry so packaging doesn't fail on a transient lock.
[GC]::Collect(); Start-Sleep -Seconds 2
for ($attempt = 1; ; $attempt++) {
    # -ErrorAction Stop: promote a lock to a terminating error the catch handles, WITHOUT Compress-Archive
    # also emitting a non-terminating error record (which would surface as noise even though we recover).
    try { Compress-Archive -Path (Join-Path $root "plugin/*") -DestinationPath $zip -CompressionLevel Optimal -ErrorAction Stop; break }
    catch { if ($attempt -ge 4) { throw }; Write-Host "  zip locked, retrying ($attempt)..."; [GC]::Collect(); Start-Sleep -Seconds 3 }
}
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ("Release zip ready: {0} ({1} MB)" -f $zip, $zipMb)
Restore-StampedManifests   # the stamp lives in the zip now; keep the working tree clean
if (-not $Publish) {
    $dirtyNow = git -C $root status --porcelain
    if ($dirtyNow) { Write-Warning "UNVERIFIED local build from a DIRTY tree (uncommitted changes) - two different trees can produce identically-named zips. Commit first for a reproducible build." }
}

# Validate the artifact BEFORE anyone can install it: it must contain the plugin manifest and both exes,
# or `/plugin install` fails on the user's machine. Cheap insurance against a silently malformed zip.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$must = @(".claude-plugin/plugin.json", "bin/CodeCompass.Cli.exe", "bin/CodeCompass.Mcp.exe", "install-codecompass.ps1")
$zf = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = $zf.Entries.FullName -replace '\\', '/'
    foreach ($m in $must) { if ($entries -notcontains $m) { throw "release zip is missing '$m' - aborting (would not install)." } }
}
finally { $zf.Dispose() }
Write-Host "Verified zip contains the manifest + both binaries."

Write-Host ""
Write-Host "Install from the zip (no .NET needed):"
Write-Host "  1. unzip it to a folder, e.g. C:\Tools\codecompass-plugin"
Write-Host "  2. in Claude Code:  /plugin marketplace add `"C:\Tools\codecompass-plugin`""
Write-Host "                then  /plugin install codecompass@codecompass"
Write-Host ""

if (-not $Publish) {
    Write-Host "Not publishing (add -Publish to upload to GitHub Releases as v$version)."
    exit 0
}

# 5) Publish to GitHub Releases via gh (create the tag/release, or upload to an existing one). The source
#    was already validated and pushed in step 0, so origin/main == $sha and the tag pins to it below.
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "gh CLI not found; install it or upload $zip manually." }
$tag = "v$version"
$sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()  # published so installers can verify the download
$notes = @"
CodeCompass $version - prebuilt, self-contained Windows (x64) plugin.

Install (no .NET SDK required). Download codecompass-plugin-$version-win-x64.zip below and unzip it,
then in Claude Code EITHER install persistently (the folder is a self-marketplace):

    /plugin marketplace add "<unzipped-folder>"
    /plugin install codecompass@codecompass

OR load it for one session only:

    claude --plugin-dir "<unzipped-folder>"

Then run /mcp to confirm the codecompass server is connected. (There is no ``/plugin add`` command.)

The ARM64 Windows build runs via x64 emulation. See the bundled CodeCompass.html for docs.

Built from commit $sha.
SHA256 (codecompass-plugin-$version-win-x64.zip): $sha256
"@

# gh writes "release not found" to stderr for a missing tag; under ErrorActionPreference=Stop that
# native stderr would fault, so probe with Continue and decide on the exit code.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh release view $tag 2>$null 1>$null
$exists = ($LASTEXITCODE -eq 0)
if ($exists) {
    Write-Host "Release $tag exists - uploading asset (clobber)..."
    gh release upload $tag $zip --clobber
} else {
    Write-Host "Creating release $tag at $sha ..."
    # --target pins the tag to the exact commit we built + just pushed (not the remote default-branch tip),
    # so the tag, the source, and the shipped binary always agree.
    gh release create $tag $zip --title "CodeCompass $version" --notes $notes --target $sha
}
$publishCode = $LASTEXITCODE
$ErrorActionPreference = $prevEap
if ($publishCode -ne 0) { throw "gh release publish failed" }
Write-Host "Published $tag with $([System.IO.Path]::GetFileName($zip))."
