# CodeCompass (Claude Code plugin)

Fast, local, indexed code search and navigation for large codebases — exposed to
Claude Code as MCP tools, with a hook that steers the agent off `grep` and onto
CodeCompass to cut token spend.

## What it gives the agent

MCP tools (returned as precise `file:line:col` results, not whole files):

- `search_code` — literal text search over the whole repo
- `find_definition` — go-to-definition by exact symbol name
- `find_references` — **semantic** for C# (Roslyn) and C/C++ (clang); lexical elsewhere
- `find_callees` — the in-repo methods a C# method calls, resolved **semantically** (walk a call chain downward)
- `search_symbols` — symbol-name navigation
- `reindex` — rebuild the index after large external changes

The index builds on first use, then **auto-updates** as files change (debounced,
content-hash verified, ignores build output). Changes made *outside* a session (a
Perforce/git sync with Claude closed) are picked up by a background **reconcile** on
startup — automatic for local repos, deferred to `codecompass update` on network
shares / huge repos (`autoReconcile` config to force it).

**Linked roots.** A project can federate external directories that can't be nested
under it (a shared library elsewhere, a sibling repo, a `Z:\` drop): `codecompass
link add "<external-path>"`. They're searched alongside the project as one result set,
live-watched for edits like the main repo, and `find_references`/`find_callees`
resolve **across** the boundary (C#/C++). A root shared by several projects is indexed
once and reused. Project hits stay repo-relative; linked hits show absolute paths.
**Multi-repo search is an MCP-server feature only** — these tools federate across a
project's linked roots; the `codecompass` CLI always searches the single root you give
it (it's a stateless one-shot, and you use it to *set up* links with `link add`). See
the main README's *Linked roots → MCP vs CLI* section.

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

> There is **no** `/plugin add "<path>"` command — that's not how Claude Code installs a local
> plugin. Use one of the two flows below. In both, the folder you point at is the one that
> directly contains `.claude-plugin\` (with `plugin.json` + `marketplace.json`), `bin\`, and
> `hooks\`. The prebuilt zip unzips to exactly that folder; a source checkout uses `<repo>\plugin`.

### Prebuilt (no .NET needed) — recommended

1. Download `codecompass-plugin-<version>-win-x64.zip` from the repo's Releases page and unzip it.
   **The unzipped folder IS the plugin** (it contains `.claude-plugin\`, `bin\`, `hooks\`); there is
   no `plugin\` subfolder (that only exists in a source checkout).

2. **Persistent install** (stays across sessions) — the folder ships a self-marketplace, so:

   ```
   /plugin marketplace add "C:\path\to\codecompass-plugin-<version>-win-x64"
   /plugin install codecompass@codecompass
   ```

   (`codecompass@codecompass` = plugin `codecompass` from the marketplace named `codecompass`.)

3. **Or, one session only** (no install) — launch Claude Code with:

   ```
   claude --plugin-dir "C:\path\to\codecompass-plugin-<version>-win-x64"
   ```

4. Run `/mcp` to confirm the `codecompass` server is connected.

### From source (needs the .NET 8 SDK)

1. Build the self-contained binaries (the *result* needs no .NET on the target machine):

   ```powershell
   pwsh ./build-plugin.ps1
   ```

   This publishes `plugin/bin/CodeCompass.Mcp.exe` and `CodeCompass.Cli.exe`.

2. Load it (the `plugin` subfolder here is the source-checkout plugin root):

   - Persistently:  `/plugin marketplace add "<repo>\plugin"` then `/plugin install codecompass@codecompass`
   - One session:   `claude --plugin-dir "<repo>\plugin"`

3. In a session, run `/mcp` to confirm the `codecompass` server is connected.

### Codex (OpenAI)

The same MCP server works in Codex — the tools are identical; only the registration differs. From the
unzipped plugin folder:

```powershell
./install-codecompass.ps1 -TargetHost Codex
```

That runs `codex mcp add codecompass -- "<folder>\bin\CodeCompass.Mcp.exe"` with the **absolute** exe path
resolved at install time (no copy step, no hardcoded location). Or register it by hand:

```
codex mcp add codecompass -- "C:\path\to\codecompass-plugin-<version>-win-x64\bin\CodeCompass.Mcp.exe"
```

No workspace argument is passed: the server indexes **Codex's working directory** (its cwd), so open Codex
in your project root. It prints the resolved root to stderr at startup, visible in Codex's MCP log, so a
wrong-directory launch is obvious. Verify with `codex mcp get codecompass`. Codex has no `PreToolUse`
equivalent, so there's no grep-enforcement hook there — the tools are available for the agent to choose.

## First use

Just work normally — ask Claude to find code, jump to definitions, or trace usages, and the hook
routes it onto CodeCompass. A small/medium repo indexes in the background on first use (a tool may
briefly say "indexing… N%"). For a **large** repo, build the index once from a terminal —
`codecompass index "C:\path\to\repo"` — then it serves and self-updates. Diagnose with
`codecompass doctor "<repo>"`; bundle a bug report (diagnostics + logs, never source) with
`codecompass report "<repo>"`.

## Notes

- Windows x64. The published `bin/` is a build artifact (gitignored); run
  `build-plugin.ps1` to (re)generate it.
- The server indexes the current workspace (`${CLAUDE_PROJECT_DIR}`, falling back to
  the working directory).
