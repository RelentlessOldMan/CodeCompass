# Publishes the CodeCompass MCP server + CLI as self-contained win-x64 binaries
# into plugin/bin, producing a ready-to-install Claude Code plugin that needs no
# separate .NET install on the target machine.
#
# Usage:  pwsh ./build-plugin.ps1

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$binDir = Join-Path $root "plugin/bin"

Write-Host "Cleaning $binDir ..."
if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
New-Item -ItemType Directory -Path $binDir | Out-Null

$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:DebugType=none", "-o", $binDir)

Write-Host "Publishing MCP server ..."
dotnet publish (Join-Path $root "src/CodeCompass.Mcp/CodeCompass.Mcp.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed" }

Write-Host "Publishing CLI ..."
dotnet publish (Join-Path $root "src/CodeCompass.Cli/CodeCompass.Cli.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

$mcp = Join-Path $binDir "CodeCompass.Mcp.exe"
$cli = Join-Path $binDir "CodeCompass.Cli.exe"
if (-not (Test-Path $mcp)) { throw "expected $mcp" }
if (-not (Test-Path $cli)) { throw "expected $cli" }

# Ship the self-contained docs alongside the plugin.
$docs = Join-Path $root "docs/CodeCompass.html"
if (Test-Path $docs) { Copy-Item $docs (Join-Path $root "plugin/CodeCompass.html") -Force }

$size = [math]::Round(((Get-ChildItem $binDir -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
$version = (& $cli version) 2>$null   # e.g. "CodeCompass 1.0.64+a1b2c3d4"

# Keep the plugin manifest version in step with the binaries: the numeric part (no +sha), so a plugin
# registry and `codecompass version` agree on the release line.
$manifestVer = ($version -replace '^CodeCompass\s+', '') -replace '\+.*$', ''
$pjPath = Join-Path $root "plugin/.claude-plugin/plugin.json"
if ($manifestVer -match '^\d+\.\d+\.\d+' -and (Test-Path $pjPath)) {
    $pj = Get-Content $pjPath -Raw
    $pj = [regex]::Replace($pj, '("version"\s*:\s*")[^"]*(")', "`${1}$manifestVer`${2}")
    Set-Content $pjPath $pj -Encoding utf8 -NoNewline
    Write-Host "Stamped plugin.json version = $manifestVer"
}

Write-Host ""
Write-Host "Plugin ready: $(Join-Path $root 'plugin')  ($version, bin is $size MB)"
Write-Host "Install for one session:   claude --plugin-dir `"$(Join-Path $root 'plugin')`""
Write-Host "Install persistently:      /plugin add `"$(Join-Path $root 'plugin')`""
