# CodeCompass

**Fast, fully-local code search & navigation for large codebases — built to cut the tokens an AI coding agent spends finding code.**

CodeCompass indexes your repo on your machine and gives Claude Code (or your terminal) precise
`file:line:col` answers — text search, go-to-definition, and find-references — instead of the agent
grepping blindly and reading whole files. **Nothing leaves your machine. No GPU. No cloud.**

An AI agent's default moves — grep for a string, then read entire files to understand a match — are
cheap on a small repo and ruinously token-hungry on a big one. CodeCompass returns just the lines
that matter, so the agent spends its context on thinking, not on scrolling files it didn't need.

## How it works

Three complementary layers, cheapest first:

- **Lexical** — a trigram index for instant literal/substring search across the whole repo.
- **Symbolic** — tree-sitter parses every file into symbols (classes, methods, functions…) for go-to-definition.
- **Semantic** — real find-references for **C#** (Roslyn) and **C/C++** (clang): it resolves the actual
  symbol and ignores matches in comments and strings, which a text search can't.

Everything is **memory-mapped on disk** — the trigram index, the symbol index, and the
change-detection ledger — so searching a dozens-of-GB repo uses only a few hundred MB of RAM and
editing it keeps just the changed files in memory. This is what lets an 87 GB repo be served from a
16 GB laptop.

## What it can & can't do

| Capability | Status |
|---|---|
| Literal / substring search (all files) | ✅ Yes |
| Go-to-definition & symbol search | ✅ C#, C, C++, Python, JS, TS/TSX, Go, Rust |
| Semantic find-references (excludes comments/strings) | ✅ C# & C/C++; lexical whole-word elsewhere |
| Auto re-index on file changes | ✅ debounced, content-hash verified, ignores build output |
| Runs fully local, no GPU, no cloud | ✅ Yes |
| Dozens-of-GB repos without exhausting RAM | ✅ indexes are memory-mapped on disk |
| MATLAB / other unlisted languages | Lexical only (text search works; no symbols) |
| C/C++ find-references precision | Best-effort without a `compile_commands.json` |
| Semantic "meaning" / embedding search | ❌ No (deliberately — needs a model; weaker for real code nav) |

## Minimal token footprint

A pain point with some in-house tools is that they register a pile of MCP tools/skills that eat
context every session. CodeCompass exposes exactly **5 tools** with terse descriptions — that's the
entire standing per-session cost: `search_code`, `find_definition`, `find_references`,
`search_symbols`, `reindex`.

## Install

Requires the .NET 8 SDK to *build*; the produced binaries are self-contained (no .NET needed to run
them). Windows x64.

> **Windows on ARM (Snapdragon):** there's no separate ARM build and you don't need one — the x64
> binaries run on Windows 11 on ARM via its built-in x64 emulation (the whole process, native
> dependencies included, runs emulated). Searches stay effectively instant; only the initial index
> build runs somewhat slower than on native x64.

```powershell
pwsh ./build-plugin.ps1      # publishes self-contained binaries into plugin/bin
```

Then load it into Claude Code:

```
claude --plugin-dir "<path>/plugin"     # one session
/plugin add "<path>/plugin"             # persistent (run inside Claude Code)
```

In a session, run `/mcp` to confirm the **codecompass** server is connected. A `PreToolUse` hook
redirects `Grep`/`Glob` to CodeCompass so the agent uses the index instead of scanning files (set
`CODECOMPASS_ENFORCE=0` to allow grep again). Large workspaces aren't auto-indexed inside a tool
call — CodeCompass tells you to build the index once from a terminal, then serves it and keeps it fresh.

## Command line

```
codecompass index   <path>            build the index (shows progress + ETA)
codecompass update  <path>            incremental reindex of changes
codecompass watch   <path>            auto-reindex on file changes
codecompass search  <path> <query>    literal text search  -> file:line:col
codecompass def     <path> <name>     go-to-definition
codecompass refs    <path> <name>     references (semantic C#/C++, lexical elsewhere)
codecompass symbols <path> <substr>   symbol-name search
codecompass logs                      show the log folder and files
```

## Tuning for huge or generated trees

Indexing is robust on ordinary source at scale, but a tree with many *dense machine-generated
files* (e.g. multi-MB register-map headers that are millions of `#define` lines) can be
pathologically slow to index. Knobs to handle that without editing source:

| Env var | Effect |
|---|---|
| `CODECOMPASS_MAX_FILE_MB` | Per-file size cap for indexing (default 5). Lower it to skip large generated files. |
| `CODECOMPASS_MAX_SYMBOL_MB` | Skip tree-sitter symbol extraction above this size (default 1). Large files are still trigram-indexed; this bounds tree-sitter's ~O(n²) parse cost so a giant generated header can't stall indexing. |
| `CODECOMPASS_IGNORE` | Comma/semicolon-separated directory names to exclude (e.g. `generated,vendor`). |
| `CODECOMPASS_STALL_WARN_SEC` | Warn in the log if indexing makes no progress for this long (default 60). |
| `CODECOMPASS_THREADS` / `CODECOMPASS_SEGMENT_MB` | Indexing parallelism / per-worker segment budget. |

Files skipped for exceeding the size cap are counted and logged (not silently dropped), so you can
see the coverage gap. Note: files over the cap are currently absent from search — searching *over*
the cap is a known limitation.

## Performance

Measured on an **Intel Core i7-8700** (6 cores / 12 threads, ~2018), 32 GB RAM, Windows 11 Pro,
.NET 8. A modern many-core laptop builds substantially faster.

> **Note:** these figures were recorded *before* the EcoQoS / E-core throttling opt-out, so build
> throughput is conservative here — expect higher on hybrid CPUs (up to ~2× when the OS was
> previously parking the process on efficiency cores).

| Repo | Lang | Source | Build | MB/s | Query p50/p95 | RAM (heap/peak) |
|---|---|--:|--:|--:|--:|--:|
| requests | Python | 2.7 MB | 1.1 s | 2.5 | 1.2 / 53 ms | 0 / 127 MB |
| fmt | C++ | 3.1 MB | 0.4 s | 8.6 | 0.9 / 3 ms | 0 / 107 MB |
| EF Core | C# | 63 MB | 3.7 s | 17 | 1.5 / 16 ms | 3 / 170 MB |
| TypeScript | TS | 325 MB | 21 s | 15 | 1.3 / 3 ms | 38 / 270 MB |
| Godot | C++ | 168 MB | 9.5 s | 18 | 0.9 / 9 ms | 5 / 587 MB |
| Roslyn | C# | 344 MB | 15 s | 23 | 1.3 / 3 ms | 12 / 332 MB |
| LLVM | C/C++ | 1.3 GB | 51 s | 25 | 0.8 / 7 ms | 63 / 482 MB |

**Scale check:** an aggregated **10.4 GB / ~1.1 million file** corpus indexed in **7m48s** (22 MB/s)
using **792 MB heap / 2.2 GB peak working set**, with queries still ~1 ms (p95 7.9 ms). Memory stays
bounded because the indexes are memory-mapped, not loaded into RAM. Build memory is tunable via
`CODECOMPASS_SEGMENT_MB` / `CODECOMPASS_THREADS`.

A one-page, self-contained version of these docs lives in [`docs/CodeCompass.html`](docs/CodeCompass.html).

## Logs

A self-limiting log lives under `%LOCALAPPDATA%\CodeCompass\logs` (run `codecompass logs` to find
it): a common `codecompass.log` (lifecycle + all warnings/errors) plus per-repo logs. Files are
size-capped and rotated (default 5 MB × 4), so logging can't grow unbounded. Tune with
`CODECOMPASS_LOG_LEVEL` / `_MAX_MB` / `_KEEP` / `_DIR` (or `CODECOMPASS_LOG=0` to disable).

## Building & testing

```powershell
dotnet build CodeCompass.sln -c Release
dotnet test  CodeCompass.sln -c Release
```

See [`DESIGN.txt`](DESIGN.txt) for the architecture and [`TESTING.txt`](TESTING.txt) for the test
plan. The optional benchmark corpus is fetched by [`fetch-corpus.ps1`](fetch-corpus.ps1) (pinned to
exact commits) and never checked in.

## License

[MIT](LICENSE).
