<#
.SYNOPSIS
  Build a one-download release of the CodeCompass plugin: the self-contained win-x64 binaries + MCP
  manifest + hooks + docs, zipped and (optionally) published to GitHub Releases so others can install
  without the .NET SDK or a source build.

.DESCRIPTION
  Runs build-plugin.ps1 (publishes plugin/bin), zips the whole plugin/ folder to
  codecompass-plugin-<version>-win-x64.zip (gitignored), and prints install steps. There is deliberately
  NO CI - you cut releases from this machine with this script.

  -Publish uploads the zip to a GitHub Release tagged v<version> via the `gh` CLI (must be installed and
  authenticated: `gh auth status`). Without -Publish it only builds the local zip.

.PARAMETER Publish   Also create/update the GitHub Release v<version> and upload the zip (needs `gh`).

.EXAMPLE
  pwsh ./make-release.ps1              # build the local release zip
  pwsh ./make-release.ps1 -Publish     # build + publish to GitHub Releases
#>
param([switch]$Publish)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# 1) Build the plugin (self-contained binaries + stamped plugin.json version).
& (Join-Path $root "build-plugin.ps1")
if ($LASTEXITCODE -ne 0) { throw "build-plugin.ps1 failed" }

# 2) Version = the numeric version build-plugin stamped into the manifest (matches `codecompass version`).
$pjPath = Join-Path $root "plugin/.claude-plugin/plugin.json"
$version = (Get-Content $pjPath -Raw | ConvertFrom-Json).version
if (-not $version) { throw "could not read version from $pjPath" }

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
    try { Compress-Archive -Path (Join-Path $root "plugin/*") -DestinationPath $zip -CompressionLevel Optimal; break }
    catch { if ($attempt -ge 4) { throw }; Write-Host "  zip locked, retrying ($attempt)..."; Start-Sleep -Seconds 3 }
}
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ("Release zip ready: {0} ({1} MB)" -f $zip, $zipMb)

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

# 4) Sync source to origin BEFORE tagging. Releases build from the local HEAD and only upload the zip,
#    so if these commits aren't pushed, origin/main stays stale (a work checkout sees old source) AND the
#    tag gh creates lands on the wrong commit. Push HEAD->main first, then tag AT this exact commit
#    (--target below), so binary, tag, and source can never drift. (This bit us once; never again.)
$sha = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "could not resolve HEAD" }

$dirty = git status --porcelain
if ($dirty) { throw "working tree has uncommitted/untracked changes - commit them before releasing:`n$dirty" }

Write-Host "Fetching origin to check main is fast-forwardable ..."
git fetch origin --quiet
if ($LASTEXITCODE -ne 0) { throw "git fetch origin failed" }

# HEAD must be at or ahead of origin/main (a clean fast-forward). If origin/main has commits this build
# doesn't, the branch diverged - stop rather than force anything.
git merge-base --is-ancestor origin/main HEAD
if ($LASTEXITCODE -ne 0) { throw "origin/main has commits not in this build (diverged) - reconcile (git pull --rebase) before releasing." }

Write-Host "Pushing $sha -> origin/main ..."
git push origin "${sha}:refs/heads/main"
if ($LASTEXITCODE -ne 0) { throw "git push to origin/main failed - resolve, then re-run." }

# 5) Publish to GitHub Releases via gh (create the tag/release, or upload to an existing one).
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "gh CLI not found; install it or upload $zip manually." }
$tag = "v$version"
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
