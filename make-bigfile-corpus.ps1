<#
.SYNOPSIS
  Generate one very large (default ~2 GB) auto-generated register-map style header, so the streaming
  large-file index + block/positional search + truncation behaviour can be replicated locally WITHOUT
  committing a multi-GB file to git. Output goes to a gitignored .corpus/ dir.

.DESCRIPTION
  Writes a single .h file of `#define HEY_MOM_MY_CHIP_REG_<n> 0x<val>` lines - every macro shares the
  long prefix HEY_MOM_MY_CHIP_, which is exactly the case that stresses search selectivity (the prefix
  trigrams are everywhere; only the distinctive suffix narrows). Three UNIQUE markers are scattered at
  ~1%, ~50% and ~99% through the file so you can verify a search finds a match near the very end
  cheaply (the positional index seeks to its block instead of re-reading the whole file).

  Nothing here is committed: .corpus/ is gitignored. Delete it when done (it's large).

.PARAMETER SizeGB   Approximate file size in GB (default 2). Use e.g. 0.2 for a quick check.
.PARAMETER OutDir   Output dir (default .corpus/_bigfile, gitignored).
.PARAMETER Run      Also index the file and run a few searches, reporting timings - demonstrates the
                    streaming build, positional (block) search, and the truncation signal. Requires the
                    CLI to be built (build-plugin.ps1, or dotnet build -c Release).

.EXAMPLE
  pwsh ./make-bigfile-corpus.ps1                 # generate a ~2 GB file
  pwsh ./make-bigfile-corpus.ps1 -SizeGB 0.5 -Run
#>
param(
    [double]$SizeGB = 2,
    [string]$OutDir,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$corpus = if ($env:CODECOMPASS_CORPUS_DIR) { $env:CODECOMPASS_CORPUS_DIR } else { Join-Path $PSScriptRoot ".corpus" }
if (-not $OutDir) { $OutDir = Join-Path $corpus "_bigfile" }
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$file = Join-Path $OutDir "chipreg_bank.h"
[long]$target = [long]($SizeGB * 1GB)

# Unique, searchable markers placed at ~1% / ~50% / ~99% of the file.
$markers = @{
    ([long]($target * 0.01)) = "#define ZZ_UNIQUE_MARKER_START_7a3f 0x00000001"
    ([long]($target * 0.50)) = "#define ZZ_UNIQUE_MARKER_MIDDLE_b91c 0x00000002"
    ([long]($target * 0.99)) = "#define ZZ_UNIQUE_MARKER_END_e5d0 0x00000003"
}
$markerBytes = @($markers.Keys | Sort-Object)
$mi = 0

Write-Host ("Generating ~{0:N1} GB register-map header at {1} ..." -f $SizeGB, $file)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$writer = [System.IO.StreamWriter]::new($file, $false, [System.Text.Encoding]::ASCII, [int]1MB)
try {
    $sb = [System.Text.StringBuilder]::new([int]4MB)
    [long]$written = 0
    [long]$i = 0
    while ($written -lt $target) {
        # Vary the name via a hex counter so distinctive suffix trigrams exist to search for.
        [void]$sb.Append("#define HEY_MOM_MY_CHIP_REG_").Append($i.ToString("X")).Append(" 0x").Append((($i * 2654435761) -band 0xFFFFFFFF).ToString("X8")).Append("`n")
        $i++
        if ($sb.Length -ge [int]4MB) {
            $s = $sb.ToString(); $writer.Write($s); $written += $s.Length; [void]$sb.Clear()
            # Emit any marker whose byte threshold we've now passed.
            while ($mi -lt $markerBytes.Count -and $written -ge $markerBytes[$mi]) {
                $line = $markers[$markerBytes[$mi]]; $writer.Write($line); $writer.Write("`n"); $written += $line.Length + 1; $mi++
            }
        }
    }
    if ($sb.Length -gt 0) { $s = $sb.ToString(); $writer.Write($s); $written += $s.Length }
} finally { $writer.Dispose() }
$sw.Stop()

$mb = [math]::Round((Get-Item $file).Length / 1MB, 0)
Write-Host ("Done: {0:N0} MB in {1:N1}s (gitignored)." -f $mb, $sw.Elapsed.TotalSeconds)
Write-Host "Markers to search for: ZZ_UNIQUE_MARKER_START_7a3f / _MIDDLE_b91c / _END_e5d0"
Write-Host ""
Write-Host "Try it (note: indexing reads the whole file once; search then reads only candidate blocks):"
Write-Host "  codecompass index  `"$OutDir`""
Write-Host "  codecompass search `"$OutDir`" ZZ_UNIQUE_MARKER_END_e5d0     # near EOF, found via positional index"
Write-Host "  codecompass search `"$OutDir`" HEY                            # matches everywhere -> capped + 'MORE EXIST'"

if (-not $Run) { exit 0 }

# Let a freshly-written multi-GB file settle (antivirus may still be scanning it, which can briefly
# hide it from the very next directory walk).
Start-Sleep -Seconds 2

$cli = @(
    (Join-Path $PSScriptRoot "plugin/bin/CodeCompass.Cli.exe"),
    (Join-Path $PSScriptRoot "src/CodeCompass.Cli/bin/Release/net8.0/CodeCompass.Cli.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cli) { Write-Warning "CLI not built (run build-plugin.ps1 or dotnet build -c Release); skipping -Run."; exit 0 }

# Let native stderr (progress bars, the '-- N match(es) / MORE EXIST' summary) flow to the console
# instead of being merged with 2>&1, which PowerShell wraps as errors and would abort under Stop.
$ErrorActionPreference = "Continue"
function Time-It([string]$label, [scriptblock]$action) {
    Write-Host ("`n== {0} ==" -f $label)
    $t = [System.Diagnostics.Stopwatch]::StartNew()
    $out = & $action
    $t.Stop()
    $out | Select-Object -Last 4 | ForEach-Object { Write-Host "  $_" }
    Write-Host ("  [{0:N1}s]" -f $t.Elapsed.TotalSeconds)
}

Time-It "index (streaming build + positional sidecar)" { & $cli index $OutDir }
Time-It "search END marker (near EOF -> few blocks read via positional index)" { & $cli search $OutDir "ZZ_UNIQUE_MARKER_END_e5d0" }
Time-It "search 'HEY' (matches everywhere -> capped + 'MORE EXIST' on stderr)" { & $cli search $OutDir "HEY" }
Write-Host "`nDelete the corpus when done:  Remove-Item `"$OutDir`" -Recurse -Force"
exit 0
