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
| Very large files | >5 MB skipped from the index; >1 MB skipped from symbols but still text-searchable (both tunable) |
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
codecompass survey  <path>            report what the size caps skip + suggest config
codecompass symstats <path>           profile symbol-file sizes + parse cost per language
codecompass parsebench                tree-sitter parse-time vs size sweep (synthetic)
codecompass logs                      show the log folder and files
codecompass version                   print the build version (e.g. 1.0.52+a76d3245)
```

### Choosing `maxSymbolMb` from data

Two diagnostics measure the exact trade-off the symbol cap controls:

- **`codecompass symstats <path>`** reports, per language, the file-size distribution (p50/p95/max)
  and whether the big files actually yield symbols and how fast they parse. It's cheap on a huge repo:
  the size distribution comes from `stat` only (no parsing), and tree-sitter runs *ignoring the cap*
  only on the **largest ~100 files per language** — the cap-relevant ones — sequentially and
  timeout-guarded, so it can neither hang nor crawl. Add `--full` to parse every file (small repos).
  The headline is two numbers — the biggest real symbol-bearing file, and the smallest file whose
  parse got slow — and a good cap sits between them.
- **`codecompass parsebench`** is a synthetic sweep: it parses progressively larger generated files
  in several shapes (ordinary code, random tokens, one long line, deeply nested delimiters, huge
  expression chains, nested templates) and prints parse time vs size.

What the sweep shows on the bundled grammar: **parse cost is linear in file size** for every shape
(no quadratic explosion) — but the *constant factor varies ~70×* by content. Ordinary code parses at
~1.8 MB/s; the worst case measured is deeply-nested C++ templates at ~0.1 MB/s (a 1 MB file ≈ 12 s).
Real symbol-bearing code is tiny (single-digit KB median), so the 1 MB default leaves a wide margin:
it covers all real symbols while bounding the parse time of any single pathological file.

## Tuning for huge or generated trees

Indexing is robust on ordinary source at scale. The one hazard is *large machine-generated files*.
Tree-sitter parse cost is **linear in file size**, but the constant factor varies ~70× by content
(measured with `parsebench` — see above): so a big file of degenerate content (e.g. deeply nested
C++ templates at ~0.1 MB/s) can take tens of seconds to parse even though it grows linearly. That is
bounded by default — **symbol extraction is skipped above `CODECOMPASS_MAX_SYMBOL_MB` (1 MB)**, and
those files are still fully trigram-indexed, so text search stays complete. In practice this never
touches hand-written code: across our test corpora *every* source file over 1 MB was machine-generated
(bundled JS, generated bindings, giant tests). The knobs below tune coverage vs. that cost without
editing source:

| Env var | Effect |
|---|---|
| `CODECOMPASS_MAX_SYMBOL_MB` | Skip tree-sitter symbol extraction above this size (default 1). Bounds per-file parse time; raise it if you have large *valid* code whose symbols you want (run `symstats` first). Raising it is safe: above 1 MB, files that are overwhelmingly numeric/hex data (generated arrays — slow to parse, zero symbols) are auto-skipped by content, so only large *real* code gets parsed. |
| `CODECOMPASS_MAX_FILE_MB` | Per-file size cap for indexing entirely (default 2000, i.e. 2 GB). Files ≥128 MB are indexed by **streaming** (bounded memory), so a high cap won't blow up RAM; its real cost is read time on a full build (large files get re-read), so lower it per-repo if you don't want big generated files indexed. |
| `CODECOMPASS_IGNORE` | Comma/semicolon-separated directory names to exclude (e.g. `generated,vendor`). |
| `CODECOMPASS_STALL_WARN_SEC` | Warn in the log if a build stalls or a single file is held longer than this (default 60, min 5). The warning names the exact file(s) each worker is stuck on, so a pathologically slow file is identified rather than guessed. |
| `CODECOMPASS_THREADS` / `CODECOMPASS_SEGMENT_MB` | Indexing parallelism / per-worker segment budget. |
| `CODECOMPASS_READ_BUDGET_MB` | Cap on in-flight file processing memory during a parallel build (default scales to RAM: ≈1/16th of available, clamped 256 MB–4 GB). Reservations count the real footprint (~3× file size: raw bytes + decoded UTF-16 string), so the budget genuinely fits files up to ≈budget/3 and prevents N cores each loading a big file at once when `MAX_FILE_MB` is large. A file bigger than the budget reserves it all and reads solo (blocking others until done) — no deadlock. |

Files skipped for exceeding a cap are counted and logged (not silently dropped), so the coverage gap
is always visible (`codecompass survey` / `codecompass logs`).

**Limitations (by design — know where the edges are):**

- Files over `MAX_FILE_MB` (default 2000 / 2 GB) are absent from search entirely.
- Files over `MAX_SYMBOL_MB` (default 1), or classified as numeric/hex data above 1 MB when the cap
  is raised, have no go-to-definition (still text-searchable). This also covers **all streamed files**
  (≥128 MB): tree-sitter needs the whole file as one string, so symbols aren't extracted for them —
  they're text-searchable only.
- Files ≥128 MB are indexed by **streaming** (bounded memory, so a 2 GB file indexes even on 16 GB).
  A search into one uses a per-file **block/positional index** (Bloom filter per ~1 MB block) to read
  only the candidate blocks — a few MB, not the whole file — so register-name lookups in a huge
  generated header stay cheap even over a **network share**. (UTF-8 files; others fall back to a
  whole-file line scan.)
- `#define`/macro definitions are **not** captured as symbols (they'd explode the symbol index on
  register-map code); the names are still findable via `search_code`.
- Precise C/C++ semantics need a compile database (`compile_commands.json`); without one it degrades
  to syntactic. No embeddings / semantic-meaning search. Single machine, single user.

### Reproducing the large / generated-file cases locally

Big corpora are never committed (`.corpus/` is gitignored); regenerate them with the bundled scripts:

- **`make-bigfile-corpus.ps1`** — writes one ~2 GB register-map header (`#define HEY_MOM_MY_CHIP_…`)
  with unique markers scattered through it, to exercise streaming indexing + the block/positional
  search. `-Run` indexes it and searches (a marker near EOF is found in ms via the positional index;
  `HEY` shows the truncation signal). `-SizeGB 0.2` for a quick check.
- **`make-pathological-corpus.ps1`** — the slow-to-parse shapes (nested templates, etc.) that stress
  tree-sitter; `-Run` demonstrates the symbol cap handling them.
- **`make-megacorpus.ps1`** — aggregates fetched repos into a >10 GB tree for scale runs.

### Per-repo config file

Every knob above also lives in an optional **`.codecompass.json`** at the repo root, so settings
travel with the repo instead of being set on every run. Precedence is **env var → config file →
default**. Fields: `maxSymbolMb`, `maxFileMb`, `maxAutoMb`, `ignore` (array of directory names),
`threads`, `segmentMb`, `compactSegments`, `stallWarnSec`, `readBudgetMb`.

```json
{ "maxSymbolMb": 4, "ignore": ["generated", "thirdparty"] }
```

Run **`codecompass survey <path>`** first — it reports what the current caps skip (files with no
go-to-definition, files absent from search), names the largest, and suggests a concrete config
change. It changes nothing; you decide. There is deliberately **no auto-bumping**: file size isn't a
reliable signal of parse safety (a valid 5 MB file parses fast, a degenerate one is ~25× slower), so
raising a cap is a judgement only the repo owner can make. Raising the symbol cap is safe from the
data-blob crawl — above 1 MB, overwhelmingly numeric/hex files are auto-skipped for symbols by
content (see *Tuning* above).

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
