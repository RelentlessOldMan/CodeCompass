# CodeCompass (Claude Code plugin)

Fast, local, indexed code search and navigation for large codebases — exposed to
Claude Code as MCP tools, with a hook that steers the agent off `grep` and onto
CodeCompass to cut token spend.

## What it gives the agent

MCP tools (returned as precise `file:line:col` results, not whole files):

- `search_code` — literal text search over the whole repo
- `find_definition` — go-to-definition by exact symbol name
- `find_references` — **semantic** for C# (Roslyn) and C/C++ (clang); lexical elsewhere
- `search_symbols` — symbol-name navigation
- `reindex` — rebuild the index after large external changes

The index builds on first use, then **auto-updates** as files change (debounced,
content-hash verified, ignores build output). Changes made *outside* a session (a
Perforce/git sync with Claude closed) are picked up by a background **reconcile** on
startup — automatic for local repos, deferred to `codecompass update` on network
shares / huge repos (`autoReconcile` config to force it).

## Status line (optional)

Show the index state in Claude Code's status area via `settings.json`:

```json
{ "statusLine": { "type": "command", "command": "codecompass statusline" } }
```

The bare command renders a self-contained line — `<model> · <cwd> · CodeCompass ✓ N
files` — since a custom status line replaces Claude's default. Already have your own
status line? Use `codecompass statusline --wrap "<your-command>"` to keep it and
append only the CodeCompass segment. See the main README for details.

## Enforcement

A `PreToolUse` hook blocks `Grep`/`Glob` and redirects the agent to the CodeCompass
tools. A `SessionStart` hook reminds the agent to prefer them.

To allow `grep` again, set the environment variable `CODECOMPASS_ENFORCE=0`.

## Install

1. Build the self-contained binaries (needs the .NET 8 SDK; the *result* needs no
   .NET on the target machine):

   ```powershell
   pwsh ./build-plugin.ps1
   ```

   This publishes `plugin/bin/CodeCompass.Mcp.exe` and `CodeCompass.Cli.exe`.

2. Load it:

   - One session:   `claude --plugin-dir "<path>/plugin"`
   - Persistently:  `/plugin add "<path>/plugin"`

3. In a session, run `/mcp` to confirm the `codecompass` server is connected.

## Notes

- Windows x64. The published `bin/` is a build artifact (gitignored); run
  `build-plugin.ps1` to (re)generate it.
- The server indexes the current workspace (`${CLAUDE_PROJECT_DIR}`, falling back to
  the working directory).
