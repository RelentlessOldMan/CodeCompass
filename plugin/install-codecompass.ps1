<#
.SYNOPSIS
  Register the CodeCompass MCP server with Codex and/or print the Claude install steps.

.DESCRIPTION
  Run this from the unzipped plugin folder (it locates bin\CodeCompass.Mcp.exe next to itself).

  Codex: installs via the native Codex PLUGIN marketplace (preferred) so Codex gets BOTH the MCP server
  AND the bundled advisory skill (skills\codecompass\SKILL.md) that steers Codex toward the tools:
      codex plugin marketplace add "<this folder>"     # this folder ships .agents\plugins\marketplace.json
      codex plugin add codecompass@codecompass         # registers the MCP server (${CODEX_PLUGIN_ROOT}\bin\...)
    - The plugin's mcp.json uses ${CODEX_PLUGIN_ROOT}, which Codex expands to this folder, so no absolute
      path is baked in and no copy step is needed. Verified: `codex mcp list` then shows the server enabled.
    - No workspace argument is passed: the server serves Codex's working directory (cwd fallback) and prints
      the resolved root to stderr at startup. Codex launches a stdio MCP server per session with the cwd.
    - FALLBACK: if the installed Codex is too old to have `codex plugin` (pre-marketplace), the script
      registers the raw MCP server via `codex mcp add codecompass -- "<abs>\bin\CodeCompass.Mcp.exe"`
      instead (server only, no skill). Idempotent either way - re-running upgrades cleanly.

  Claude: does NOT touch Claude config; it prints the marketplace commands to run (Claude owns that flow).

.PARAMETER TargetHost   Which host(s) to set up: Codex, Claude, or Both (default Both).
.PARAMETER Name         Codex MCP server name (default 'codecompass').
.EXAMPLE
  ./install-codecompass.ps1                 # both hosts
  ./install-codecompass.ps1 -TargetHost Codex
  ./install-codecompass.ps1 -TargetHost Codex -WhatIf   # preview only
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Codex', 'Claude', 'Both')][string]$TargetHost = 'Both',
    [string]$Name = 'codecompass'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'bin/CodeCompass.Mcp.exe'

function Test-CodexInstall {
    $exeFull = (Resolve-Path -LiteralPath $exe -ErrorAction SilentlyContinue)
    if (-not $exeFull) { throw "CodeCompass.Mcp.exe not found at '$exe'. Run this from the unzipped plugin folder." }
    $exeFull = $exeFull.Path

    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if (-not $codex) {
        Write-Warning "codex CLI not found on PATH. Install it (npm i -g @openai/codex), then re-run. Skipping Codex."
        return
    }

    # Lock preflight: the exe can't be overwritten/registered cleanly while a prior copy is running.
    $running = Get-Process -Name 'CodeCompass.Mcp' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exeFull }
    if ($running) {
        throw "CodeCompass.Mcp.exe is running from this folder (pid $($running.Id -join ', ')). Close Codex threads using it, then re-run."
    }

    # Prefer the native plugin marketplace (ships the MCP server AND the steering skill). Detect support
    # by probing `codex plugin --help`; fall back to raw `codex mcp add` on older Codex builds.
    & codex plugin --help *> $null
    $hasPlugin = ($LASTEXITCODE -eq 0)

    if ($hasPlugin) {
        if ($PSCmdlet.ShouldProcess("Codex", "install plugin '$Name' from local marketplace '$root'")) {
            # Idempotent: re-adding a marketplace / plugin already present is tolerated (Codex reports it).
            & codex plugin marketplace add "$root" *> $null
            & codex plugin add "$Name@codecompass" *> $null
            if ($LASTEXITCODE -ne 0) { try { & codex plugin add $Name --marketplace codecompass *> $null } catch { } }
            Write-Host "Installed Codex plugin '$Name' from $root (MCP server + skill)." -ForegroundColor Green
            Write-Host "Verify:  codex plugin list    |    codex mcp list"
            Write-Host "Note: the server serves Codex's working directory; open Codex in your project root."
        }
        return
    }

    if ($PSCmdlet.ShouldProcess("Codex", "register MCP server '$Name' -> $exeFull (fallback: no plugin subcommand)")) {
        # Older Codex without `codex plugin`: idempotent upsert of the raw MCP server (no skill).
        try { & codex mcp remove $Name 2>$null | Out-Null } catch { }
        & codex mcp add $Name -- $exeFull
        if ($LASTEXITCODE -ne 0) { throw "codex mcp add failed (exit $LASTEXITCODE)." }
        Write-Host "Registered '$Name' with Codex -> $exeFull (raw MCP; upgrade Codex for the skill)." -ForegroundColor Green
        Write-Host "Verify:  codex mcp get $Name    |    codex mcp list"
    }
}

function Show-ClaudeInstall {
    Write-Host ""
    Write-Host "Claude Code (run these yourself; this script does not modify Claude config):" -ForegroundColor Cyan
    Write-Host "  /plugin marketplace add `"$root`""
    Write-Host "  /plugin install codecompass@codecompass"
}

if ($TargetHost -in 'Codex', 'Both') { Test-CodexInstall }
if ($TargetHost -in 'Claude', 'Both') { Show-ClaudeInstall }
