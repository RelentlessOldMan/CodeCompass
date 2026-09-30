<#
.SYNOPSIS
  Collects line/branch/method coverage for the CodeCompass test suite via coverlet, then
  renders an HTML + text-summary report with ReportGenerator.

.NOTES
  This runs the FULL test suite under instrumentation - it is machine-saturating and MUST be
  gated through ComputeWarden (see CLAUDE.md). Do not invoke inside a warden-BUSY window.

  Usage:
    pwsh scripts/coverage.ps1                 # full run -> coverage/report/index.html + Summary.txt
    pwsh scripts/coverage.ps1 -Label baseline # tags the output dir so BEFORE/AFTER can be compared
#>
[CmdletBinding()]
param(
  [string]$Label = "current",
  [string]$Filter = ""   # optional dotnet test --filter expression
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$testProj = Join-Path $root "tests\CodeCompass.Core.Tests\CodeCompass.Core.Tests.csproj"
$outRoot = Join-Path $root "coverage\$Label"
$resultsDir = Join-Path $outRoot "raw"
$reportDir = Join-Path $outRoot "report"

if (Test-Path $outRoot) { Remove-Item $outRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

Write-Host "== Collecting coverage ($Label) =="
$testArgs = @(
  "test", $testProj,
  "-c", "Debug",
  "--collect:XPlat Code Coverage",
  "--results-directory", $resultsDir,
  "--", "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura"
)
if ($Filter) { $testArgs = $testArgs[0..1] + @("--filter", $Filter) + $testArgs[2..($testArgs.Length-1)] }
& dotnet @testArgs
$testExit = $LASTEXITCODE

$cobertura = Get-ChildItem -Path $resultsDir -Recurse -Filter "coverage.cobertura.xml" | Select-Object -First 1
if (-not $cobertura) { Write-Error "No cobertura file produced under $resultsDir"; exit 1 }
Write-Host "Cobertura: $($cobertura.FullName)"

# ReportGenerator (global tool). Install once with: dotnet tool install -g dotnet-reportgenerator-globaltool
$rg = Get-Command reportgenerator -ErrorAction SilentlyContinue
if ($rg) {
  & reportgenerator "-reports:$($cobertura.FullName)" "-targetdir:$reportDir" "-reporttypes:Html;TextSummary;MarkdownSummaryGithub"
  $summary = Join-Path $reportDir "Summary.txt"
  if (Test-Path $summary) { Write-Host "== Coverage summary ($Label) =="; Get-Content $summary }
} else {
  Write-Warning "reportgenerator not found; raw cobertura only. Install: dotnet tool install -g dotnet-reportgenerator-globaltool"
}

exit $testExit
