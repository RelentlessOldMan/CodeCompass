<#
.SYNOPSIS
  Fabricate the "crazy" inputs that stress CodeCompass indexing, so anyone can reproduce them locally
  WITHOUT committing giant generated files to git.

.DESCRIPTION
  The nasty real-world case was a tree of large, machine-generated headers. The corrected finding
  (measured with `codecompass parsebench`):

    * tree-sitter parse cost is LINEAR in file size - NOT O(n^2) - on the bundled grammar. But the
      constant factor varies ~70x by content. The genuinely slow shape is DEEPLY NESTED C++ TEMPLATES
      (A<A<A<...>>>) at ~0.1 MB/s, so a multi-MB header of that shape takes tens of seconds to parse.
      As a .h it hits the C++ parser; that is the stall. (Fixed: symbol extraction is skipped above
      CODECOMPASS_MAX_SYMBOL_MB, default 1 MB - the file is still trigram-indexed and text-searchable.)
    * High-entropy random tokens (the old "hang" shape) actually parse FAST (~7 MB/s) on the current
      grammar - kept below only for contrast, no longer the pathology.
    * Files just under the 5 MB size cap ARE indexed; files over it are skipped.

  This script writes synthetic files with those shapes into a gitignored dir (.corpus/ by default),
  so the repo never carries the bulk. Content is generated - it is NOT real code.

  Files it creates under <OutDir>:
    slow_nested_templates/  N x ~SizeMB .h files of deeply nested template angle brackets. THIS is the
                            slow-parse shape: as .h it parses at ~0.1 MB/s (tens of seconds for a few
                            MB) unless the symbol cap skips it. The same bytes as .txt are not parsed
                            at all - proof the cost is tree-sitter, not the trigram path.
    high_entropy/           N x ~SizeMB .h files of high-vocabulary random tokens. Historically blamed,
                            but measured fast (~7 MB/s) on the current grammar - kept for contrast.
    dense_defines/          N valid `#define REG_...` headers (~SizeMB). Benign - index fast even as .h.
    over_cap/               One file just OVER MaxFileMb, to exercise the size-skip + its log line.

.PARAMETER OutDir   Output dir (default .corpus/_pathological). Kept under .corpus/ so it's gitignored.
.PARAMETER Count    Number of files per category (default 20).
.PARAMETER SizeMB   Approx size of each generated file in MB (default 4; keep < 5 to stay under the cap).
.PARAMETER MaxFileMb  Size of the single over-cap file (default 6).
.PARAMETER Run      Also index slow_nested_templates twice - once with the symbol cap disabled
                    (reproduces the slow parse) and once at the default cap (handled) - and report
                    timing. Requires the CLI to be built (plugin/bin or src/.../bin/Release).

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

# Deeply nested template angle brackets: "A x = A<A<A<...int...>>>;". As a C/C++ header this is the
# slow-parse shape (~0.1 MB/s) - the C++ '<' is ambiguous (less-than vs template) so deep nesting is
# the parser's worst case. Each nesting level is "A<" (2 bytes) + a trailing ">" (1 byte) = 3 bytes.
function New-NestedTemplateFile([string]$path, [long]$sizeBytes) {
    $depth = [int][math]::Floor($sizeBytes / 3)
    $content = "A x = " + ("A<" * $depth) + "int" + (">" * $depth) + ";`n"
    [System.IO.File]::WriteAllText($path, $content, [System.Text.Encoding]::ASCII)
}

# Write ~sizeBytes of random tokens from the charset, newline every ~80 chars. High vocabulary, but
# measured FAST on the current grammar - kept only for contrast with the nested-template shape.
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
            $a = ([long]$i * 2654435761L) % 4294967296L
            $b = ([long]$i * 40503L + 12345L) % 4294967296L
            $line = "#define REG_F{0}_{1:X8}_{2:D6} 0x{3:X8}" -f $seed, $a, $i, $b
            $sw.WriteLine($line); $written += $line.Length + 2; $i++
        }
    } finally { $sw.Dispose() }
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$nt = Join-Path $OutDir "slow_nested_templates"; New-Item -ItemType Directory -Force -Path $nt | Out-Null
$hi = Join-Path $OutDir "high_entropy";          New-Item -ItemType Directory -Force -Path $hi | Out-Null
$df = Join-Path $OutDir "dense_defines";          New-Item -ItemType Directory -Force -Path $df | Out-Null
$oc = Join-Path $OutDir "over_cap";               New-Item -ItemType Directory -Force -Path $oc | Out-Null

$sizeBytes = [long]($SizeMB * 1MB)
Write-Host "Generating $Count x ~$SizeMB MB nested-template .h files (the slow-parse shape)..."
for ($s = 0; $s -lt $Count; $s++) { New-NestedTemplateFile (Join-Path $nt ("nest_{0:D3}.h" -f $s)) $sizeBytes }
Write-Host "Generating $Count x ~$SizeMB MB high-entropy .h files (fast on current grammar - contrast)..."
for ($s = 0; $s -lt $Count; $s++) { New-HighEntropyFile (Join-Path $hi ("chipreg_{0:D3}.h" -f $s)) $s $sizeBytes }
Write-Host "Generating $Count x ~$SizeMB MB valid dense #define headers (benign contrast)..."
for ($s = 0; $s -lt $Count; $s++) { New-DenseDefineFile (Join-Path $df ("regs_{0:D3}.h" -f $s)) $s $sizeBytes }
Write-Host "Generating one ~$MaxFileMb MB file just over the size cap..."
New-NestedTemplateFile (Join-Path $oc "huge_generated.h") ([long]($MaxFileMb * 1MB))

$total = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host ""
Write-Host "Pathological corpus ready: $OutDir  (~$total MB, gitignored)"
Write-Host ""
Write-Host "Reproduce / observe:"
Write-Host "  # Slow parse (disable the symbol cap so tree-sitter parses the nested-template files):"
Write-Host "  `$env:CODECOMPASS_MAX_SYMBOL_MB=999; codecompass index `"$nt`"    # tens of seconds per MB"
Write-Host "  # Handled (default cap skips symbol extraction; still trigram-indexed):"
Write-Host "  Remove-Item Env:\CODECOMPASS_MAX_SYMBOL_MB; codecompass index `"$nt`"   # completes fast"
Write-Host "  # Characterize the parse-cost curve directly:  codecompass parsebench .h"

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
    $job = Start-Job -ScriptBlock { param($cli, $dir) & $cli index $dir 2>$null } -ArgumentList $cli, $nt
    if (Wait-Job $job -Timeout 300) { Receive-Job $job | Out-Null } else { Stop-Job $job; $done = $false }
    Remove-Job $job -Force
    $sw.Stop()
    foreach ($k in $envOverrides.Keys) { Remove-Item "Env:\$k" -ErrorAction SilentlyContinue }
    if ($done) { Write-Host ("  {0,-32} {1,6:N1}s" -f $label, $sw.Elapsed.TotalSeconds) }
    else       { Write-Host ("  {0,-32} STALLED (killed at 300s)" -f $label) }
}

Write-Host ""
Write-Host "Demonstration ($Count nested-template files):"
Measure-Index "symbol cap DISABLED (slow parse)" @{ CODECOMPASS_MAX_SYMBOL_MB = "999" }
Measure-Index "symbol cap default (fixed)"        @{ CODECOMPASS_MAX_SYMBOL_MB = "1" }
exit 0
