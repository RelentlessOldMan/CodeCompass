<#
.SYNOPSIS
  Download the benchmark corpora listed in bench/corpus-manifest.json.

.DESCRIPTION
  Self-contained: needs only Windows PowerShell + tar (built into Windows 10 1803+).
  It does NOT require building the project - it reads the manifest, downloads each
  repo as a pinned GitHub tarball, verifies the checksum if one is pinned, and
  extracts it into the corpus cache (.corpus/ by default, gitignored).

  Use this on a fresh machine to pull the test repos down before benchmarking.

.PARAMETER Tier
  Which corpora to fetch: "all" (default), a tier ("small" | "medium" | "large"),
  or a single corpus id (e.g. "requests").

.PARAMETER CorpusDir
  Where to extract. Defaults to $env:CODECOMPASS_CORPUS_DIR, else .corpus next to this script.

.EXAMPLE
  .\fetch-corpus.ps1 small
.EXAMPLE
  .\fetch-corpus.ps1 all
.EXAMPLE
  .\fetch-corpus.ps1 llvm
#>
param(
    [string]$Tier = "all",
    [string]$CorpusDir
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = "SilentlyContinue"  # much faster downloads in Windows PowerShell

$manifestPath = Join-Path $PSScriptRoot "bench\corpus-manifest.json"
if (-not (Test-Path $manifestPath)) { throw "manifest not found: $manifestPath" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if (-not $CorpusDir) {
    $CorpusDir = if ($env:CODECOMPASS_CORPUS_DIR) { $env:CODECOMPASS_CORPUS_DIR } else { Join-Path $PSScriptRoot ".corpus" }
}
New-Item -ItemType Directory -Force -Path $CorpusDir | Out-Null

if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
    throw "tar not found. Windows 10 1803+ includes it; otherwise install Git for Windows."
}

$targets = $manifest.corpora | Where-Object { $Tier -eq "all" -or $_.tier -eq $Tier -or $_.id -eq $Tier }
if (-not $targets) { throw "no corpora match '$Tier'. Use: all | small | medium | large | <id>" }

$done = @(); $failed = @()

foreach ($c in $targets) {
    $dest = Join-Path $CorpusDir $c.id

    if ((Test-Path $dest) -and (Get-ChildItem $dest -Force -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        Write-Host "[$($c.id)] already present -> $dest"
        $done += $c.id
        continue
    }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null

    $url = "https://codeload.github.com/$($c.owner)/$($c.repo)/tar.gz/$($c.ref)"
    $tgz = Join-Path $CorpusDir ("{0}.tar.gz" -f $c.id)

    try {
        Write-Host "[$($c.id)] downloading $url"
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add("User-Agent", "CodeCompass-fetch/0.1")
        $wc.DownloadFile($url, $tgz)   # streams to disk (handles large tarballs)

        $sha = (Get-FileHash -Algorithm SHA256 -Path $tgz).Hash.ToLower()
        if ($c.sha256 -and ($c.sha256.ToLower() -ne $sha)) {
            Remove-Item $tgz -Force
            throw "checksum mismatch: manifest $($c.sha256) vs downloaded $sha (upstream tag may have moved)"
        }
        Write-Host "[$($c.id)] sha256=$sha"

        Write-Host "[$($c.id)] extracting"
        tar -xzf $tgz -C $dest
        Remove-Item $tgz -Force

        $root = (Get-ChildItem $dest -Directory | Select-Object -First 1).FullName
        if (-not $root) { $root = $dest }
        Write-Host "[$($c.id)] ready -> $root"
        $done += $c.id
    }
    catch {
        Write-Warning "[$($c.id)] FAILED: $($_.Exception.Message)"
        $failed += $c.id
        if (Test-Path $tgz) { Remove-Item $tgz -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ""
Write-Host "corpus dir: $CorpusDir"
Write-Host ("fetched: {0}" -f ($(if ($done) { $done -join ', ' } else { '(none)' })))
if ($failed) { Write-Host ("failed:  {0}" -f ($failed -join ', ')) }
