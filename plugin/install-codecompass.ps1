<#
.SYNOPSIS
  Register the CodeCompass MCP server with Codex and/or print the Claude install steps.

.DESCRIPTION
  Run this from the unzipped plugin folder (it locates bin\CodeCompass.Mcp.exe next to itself).

  Codex: registers the server via `codex mcp add codecompass -- "<abs path>\bin\CodeCompass.Mcp.exe"`.
    - The ABSOLUTE exe path is resolved at install time, so the server starts no matter what directory
      `codex` is invoked from (no hardcoded %USERPROFILE% assumption, no copy step).
    - No workspace argument is passed: the server serves Codex's working directory (its cwd fallback) and
      prints the resolved root to stderr at startup, so a wrong-directory launch is obvious in Codex's MCP
      log. Codex launches a stdio MCP server per session with the session's cwd, so it targets that project.
    - Idempotent: an existing `codecompass` entry is removed first, so re-running upgrades cleanly.

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

    if ($PSCmdlet.ShouldProcess("Codex", "register MCP server '$Name' -> $exeFull")) {
        # Idempotent upsert: drop any prior entry (ignore if absent), then add the current absolute path.
        try { & codex mcp remove $Name 2>$null | Out-Null } catch { }
        & codex mcp add $Name -- $exeFull
        if ($LASTEXITCODE -ne 0) { throw "codex mcp add failed (exit $LASTEXITCODE)." }
        Write-Host "Registered '$Name' with Codex -> $exeFull" -ForegroundColor Green
        Write-Host "Verify:  codex mcp get $Name    |    codex mcp list"
        Write-Host "Note: the server serves Codex's working directory; open Codex in your project root."
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
