<#
.SYNOPSIS
  Fabricate the "crazy" inputs that historically broke CodeCompass indexing, so anyone can
  reproduce them locally WITHOUT committing giant generated files to git.

.DESCRIPTION
  The nasty real-world case was a tree of dense, machine-generated register-map headers. Two
  properties mattered, isolated during the investigation:

    * tree-sitter parse cost is ~O(n^2) on such content -> a single multi-MB header could
      stall the whole index. THIS is the hang. (Fixed: symbol extraction is skipped above
      CODECOMPASS_MAX_SYMBOL_MB, default 1 MB.)
    * files just under the 5 MB size cap ARE indexed; files over it are skipped.

  This script writes synthetic files with that shape into a gitignored dir (.corpus/ by default),
  so the repo never carries the bulk. Content is randomly generated - it is NOT real code.

  Files it creates under <OutDir>:
    hang_high_entropy/   N x ~SizeMB .h files of high-vocabulary random tokens. As .h these hit
                         the tree-sitter O(n^2) path (the hang, when the symbol cap is disabled);
                         the identical bytes as .txt index instantly - that contrast is the proof
                         the hang is tree-sitter, not the trigram path.
    dense_defines/       N valid `#define REG_... ` headers (~SizeMB, low vocabulary). These are
                         the benign shape - they index fast even as .h - included for contrast.
    over_cap/            One file just OVER MaxFileMb, to exercise the size-skip + its log line.

.PARAMETER OutDir   Output dir (default .corpus/_pathological). Kept under .corpus/ so it's gitignored.
.PARAMETER Count    Number of files per category (default 20).
.PARAMETER SizeMB   Approx size of each generated file in MB (default 4; keep < 5 to stay under the cap).
.PARAMETER MaxFileMb  Size of the single over-cap file (default 6).
.PARAMETER Run      Also index hang_high_entropy twice - once with the symbol cap disabled (reproduces
                    the historical hang) and once at the default cap (handled) - and report timing.
                    Requires the CLI to be built (plugin/bin or src/.../bin/Release).

.EXAMPLE
  pwsh ./make-pathological-corpus.ps1
  pwsh ./make-pathological-corpus.ps1 -Count 20 -SizeMB 4 -Run
#>
param(
    [string]$OutDir,
    [int]$Count = 20,
    [double]$SizeMB = 4,
    [double]$MaxFileMb = 6,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$corpus = if ($env:CODECOMPASS_CORPUS_DIR) { $env:CODECOMPASS_CORPUS_DIR } else { Join-Path $PSScriptRoot ".corpus" }
if (-not $OutDir) { $OutDir = Join-Path $corpus "_pathological" }

$charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_".ToCharArray()

# Write ~sizeBytes of random tokens from the charset, newline every ~80 chars. High-entropy content
# maximizes the distinct-token vocabulary that makes tree-sitter's C/C++ parse go quadratic.
function New-HighEntropyFile([string]$path, [int]$seed, [long]$sizeBytes) {
    $rnd = [System.Random]::new($seed)
    $sw = [System.IO.StreamWriter]::new($path, $false, [System.Text.Encoding]::ASCII)
    try {
        $buf = [char[]]::new(65536)
        [long]$written = 0; $col = 0
        while ($written -lt $sizeBytes) {
            for ($i = 0; $i -lt $buf.Length; $i++) {
                if ($col -ge 80) { $buf[$i] = "`n"; $col = 0 }
                else { $buf[$i] = $charset[$rnd.Next($charset.Length)]; $col++ }
            }
            $sw.Write($buf, 0, $buf.Length); $written += $buf.Length
        }
    } finally { $sw.Dispose() }
}

# Valid, low-vocabulary #define header (the benign shape - fast even as .h).
function New-DenseDefineFile([string]$path, [int]$seed, [long]$sizeBytes) {
    $sw = [System.IO.StreamWriter]::new($path, $false, [System.Text.Encoding]::ASCII)
    try {
        [long]$written = 0; $i = 0
        while ($written -lt $sizeBytes) {
            $line = "#define REG_F{0}_{1:X8}_{2:D6} 0x{3:X8}" -f $seed, (($i * 2654435761) % 4294967296), $i, (($i * 40503 + 12345) % 4294967296)
            $sw.WriteLine($line); $written += $line.Length + 2; $i++
        }
    } finally { $sw.Dispose() }
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$hi = Join-Path $OutDir "hang_high_entropy"; New-Item -ItemType Directory -Force -Path $hi | Out-Null
$df = Join-Path $OutDir "dense_defines";     New-Item -ItemType Directory -Force -Path $df | Out-Null
$oc = Join-Path $OutDir "over_cap";          New-Item -ItemType Directory -Force -Path $oc | Out-Null

$sizeBytes = [long]($SizeMB * 1MB)
Write-Host "Generating $Count x ~$SizeMB MB high-entropy .h files (the tree-sitter hang shape)..."
for ($s = 0; $s -lt $Count; $s++) { New-HighEntropyFile (Join-Path $hi ("chipreg_{0:D3}.h" -f $s)) $s $sizeBytes }
Write-Host "Generating $Count x ~$SizeMB MB valid dense #define headers (benign contrast)..."
for ($s = 0; $s -lt $Count; $s++) { New-DenseDefineFile (Join-Path $df ("regs_{0:D3}.h" -f $s)) $s $sizeBytes }
Write-Host "Generating one ~$MaxFileMb MB file just over the size cap..."
New-HighEntropyFile (Join-Path $oc "huge_generated.h") 999 ([long]($MaxFileMb * 1MB))

$total = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host ""
Write-Host "Pathological corpus ready: $OutDir  (~$total MB, gitignored)"
Write-Host ""
Write-Host "Reproduce / observe:"
Write-Host "  # Historical HANG (disable the symbol cap so tree-sitter parses the huge files):"
Write-Host "  `$env:CODECOMPASS_MAX_SYMBOL_MB=999; codecompass index `"$hi`"    # was: never finishes"
Write-Host "  # Handled (default cap skips symbol extraction; still trigram-indexed):"
Write-Host "  Remove-Item Env:\CODECOMPASS_MAX_SYMBOL_MB; codecompass index `"$hi`"   # completes fast"
Write-Host "  # Proof it's tree-sitter, not text search: the same bytes as .txt are always fast."

if (-not $Run) { exit 0 }

# --- optional demonstration ---
$cli = @(
    (Join-Path $PSScriptRoot "plugin/bin/CodeCompass.Cli.exe"),
    (Join-Path $PSScriptRoot "src/CodeCompass.Cli/bin/Release/net8.0/CodeCompass.Cli.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cli) { Write-Warning "CLI not built (run build-plugin.ps1 or dotnet build -c Release); skipping -Run."; exit 0 }

function Measure-Index([string]$label, [hashtable]$envOverrides) {
    foreach ($k in $envOverrides.Keys) { Set-Item "Env:\$k" $envOverrides[$k] }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $done = $true
    $job = Start-Job -ScriptBlock { param($cli, $dir) & $cli index $dir 2>$null } -ArgumentList $cli, $hi
    if (Wait-Job $job -Timeout 120) { Receive-Job $job | Out-Null } else { Stop-Job $job; $done = $false }
    Remove-Job $job -Force
    $sw.Stop()
    foreach ($k in $envOverrides.Keys) { Remove-Item "Env:\$k" -ErrorAction SilentlyContinue }
    if ($done) { Write-Host ("  {0,-32} {1,6:N1}s" -f $label, $sw.Elapsed.TotalSeconds) }
    else       { Write-Host ("  {0,-32} HANG (killed at 120s)" -f $label) }
}

Write-Host ""
Write-Host "Demonstration ($Count high-entropy files):"
Measure-Index "symbol cap DISABLED (historical)" @{ CODECOMPASS_MAX_SYMBOL_MB = "999" }
Measure-Index "symbol cap default (fixed)"        @{ CODECOMPASS_MAX_SYMBOL_MB = "1" }
exit 0
