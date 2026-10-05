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
| Go-to-definition & symbol search | ✅ C#, C, C++, Python, JS, TS/TSX, Go, Rust, TRACE32 PRACTICE (.cmm) |
| Semantic find-references (excludes comments/strings) | ✅ C# & C/C++; lexical whole-word elsewhere |
| Auto re-index on file changes | ✅ debounced, content-hash verified, ignores build output |
| Federate external directories (linked roots) | ✅ shared-once index, live-watched, cross-root C#/C++ references |
| Runs fully local, no GPU, no cloud | ✅ Yes |
| Dozens-of-GB repos without exhausting RAM | ✅ indexes are memory-mapped on disk |
| MATLAB / other unlisted languages | Lexical only (text search works; no symbols) |
| Very large files | Indexed up to **2 GB** (streamed above ~128 MB, bounded memory); symbols skipped above 1 MB but still text-searchable (both tunable) |
| C/C++ find-references precision | Best-effort without a `compile_commands.json` (find_references says so, and `doctor` warns) |
| Semantic "meaning" / embedding search | ❌ No (deliberately — needs a model; weaker for real code nav) |

## Minimal token footprint

A pain point with some in-house tools is that they register a pile of MCP tools/skills that eat
context every session. CodeCompass exposes a deliberately small **7 tools** with terse descriptions —
that's the entire standing per-session cost: `search_code`, `find_definition`, `find_references`,
`find_callees`, `search_symbols`, `reindex`, `manage_links`.

## Install

> There is **no** `/plugin add "<path>"` command. Install a local plugin either persistently via a
> marketplace (the folder ships one) or for a single session via `--plugin-dir`, as shown below.

**Option A — prebuilt release (no .NET needed), recommended.** Download the latest
`codecompass-plugin-<version>-win-x64.zip` from the [Releases](../../releases) page and unzip it. **The
unzipped folder itself is the plugin** — it directly contains `.claude-plugin\`, `bin\`, and `hooks\`;
there is **no** `plugin\` subfolder. Then, in Claude Code, install it persistently (the folder is a
self-marketplace):

```
/plugin marketplace add "C:\path\to\codecompass-plugin-<version>-win-x64"
/plugin install codecompass@codecompass
```

…or load it for just one session by launching `claude --plugin-dir "C:\path\to\codecompass-plugin-<version>-win-x64"`.
The binaries are self-contained Windows x64. Run `/mcp` to confirm the **codecompass** server connected.

**Option B — build from source.** Requires the **.NET 10 SDK** to *build* (the CLI and MCP server target
`net10.0`; the core libraries target `net8.0`). A `global.json` pins the SDK major so the build fails with
a clear message on an older SDK. The produced binaries are self-contained.

```powershell
pwsh ./build-plugin.ps1      # publishes self-contained binaries into plugin/bin
```

Then load it into Claude Code — here the `plugin` subfolder is a **source-checkout** path (the prebuilt
zip has no such subfolder, see Option A):

```
claude --plugin-dir "<repo>\plugin"                                  # one session
/plugin marketplace add "<repo>\plugin"                              # persistent: register…
/plugin install codecompass@codecompass                             # …then install (run inside Claude Code)
```

> **Windows on ARM (Snapdragon):** there's no separate ARM build and you don't need one — the x64
> binaries run on Windows 11 on ARM via its built-in x64 emulation (the whole process, native
> dependencies included, runs emulated). Searches stay effectively instant; only the initial index
> build runs somewhat slower than on native x64.

> **Maintainer:** every push to `main` is a release - `.github/workflows/release.yml` runs
> `make-release.ps1 -Publish` on a GitHub Windows runner (full test gate, then the zip is uploaded to a GitHub
> Release tagged `v<version>`; a failed gate publishes nothing). Locally, `pwsh ./make-release.ps1` just builds the zip.

In a session, run `/mcp` to confirm the **codecompass** server is connected. A `PreToolUse` hook
redirects `Grep` to CodeCompass so the agent uses the index instead of scanning files (set
`CODECOMPASS_ENFORCE=0` to allow grep again). `Glob` is left alone — CodeCompass searches file *contents*
and symbols, not file *names*. Large workspaces aren't auto-indexed inside a tool
call — CodeCompass tells you to build the index once from a terminal, then serves it and keeps it fresh.

## Quick start (first run)

1. **Install** the plugin (above) and open Claude Code in your repo.
2. Run **`/mcp`** — confirm `codecompass` is connected.
3. **Just work normally.** Ask Claude to find things, jump to definitions, or trace usages; the hook
   steers it onto CodeCompass automatically. A small/medium repo is indexed in the background on first
   use (a tool call may briefly report "indexing… N%" — retry in a moment).
4. **Big repo?** If a tool says the workspace is large, build the index once from a terminal:
   `codecompass index "C:\path\to\repo"` (shows progress + ETA), then it serves and self-updates.
5. **Sanity check from the terminal** (optional):
   ```
   codecompass def    "C:\path\to\repo" SomeClassName     # -> file:startLine-endLine (+ inlined if small)
   codecompass search "C:\path\to\repo" "some text" -i    # -i = case-insensitive
   codecompass doctor "C:\path\to\repo"                   # health + what's indexed
   ```

**Day to day:** while a session is open, edits re-index automatically; changes made *outside* a session
(a branch switch, a source-control sync with Claude closed) are picked up by a background reconcile on
startup for local repos, or a manual `codecompass update "<repo>"` for network shares / huge repos.
Hit something odd? `codecompass doctor "<repo>"`; filing a bug? `codecompass report "<repo>"` zips
diagnostics + logs (never your source).

## Command line

```
codecompass index   <path>            build the index (shows progress + ETA)
codecompass update  <path>            incremental reindex of changes
codecompass link    <add|remove|list> <path>   federate an external directory into a project (see "Linked roots")
codecompass watch   <path>            auto-reindex on file changes
codecompass search  <path> <query> [-i]  literal text search (-i = case-insensitive)
codecompass def     <path> <name>     go-to-definition (file:startLine-endLine)
codecompass refs    <path> <name>     references (semantic C#/C++, lexical elsewhere)
codecompass callees <path> <name>     in-repo methods a C# method calls (C# only)
codecompass symbols <path> <substr>   symbol-name search
codecompass survey  <path>            report what the size caps skip + suggest config
codecompass init    <path>            write a documented .codecompass.json to edit
codecompass symstats <path>           profile symbol-file sizes + parse cost per language
codecompass parsebench                tree-sitter parse-time vs size sweep (synthetic)
codecompass statusline [--wrap "<cmd>"]  Claude Code status-line segment (index state)
codecompass doctor  <path>            diagnose a repo's index (health + metadata)
codecompass cache   [list|gc|clear <path>|clear-all]   inspect/manage the per-user index cache
codecompass report  <path> [--no-logs]  zip diagnostics + logs for a bug report (never source)
codecompass logs                      show the log folder and files
codecompass version                   print the build version (1.0.<commit-count>+<short-sha>)
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
| `CODECOMPASS_KEEP` | Comma/semicolon-separated directory names to index **even though they're skipped by default** as build output — e.g. `packages` for a pnpm/yarn monorepo whose sources live under `packages/`. (Config: `keepDirs`.) A zero result says when directories named `packages`/`build`/`out`/`target`/`dist` were skipped, so this is never a silent gap. |
| `CODECOMPASS_CACHE_DIR` | Where indexes and logs live (default `%LOCALAPPDATA%\CodeCompass`). Keep it on a **local** disk: the cross-process write lock relies on local file-handle semantics. |
| `CODECOMPASS_COMPACT_SEGMENTS` | Merge on-disk segments after this many accumulate from incremental edits (default 64). |
| `CODECOMPASS_SEMANTIC_IDLE_MIN` | Minutes the server keeps the C#/C++ semantic model resident with no `find_references` before freeing it (default 10; 0 = keep). |
| `CODECOMPASS_MAX_AUTO_MB` | Workspaces larger than this (default 100 MB) are left for a one-time CLI `codecompass index` instead of auto-indexing inside a tool call. |
| `CODECOMPASS_STALL_WARN_SEC` | Warn in the log if a build stalls or a single file is held longer than this (default 60, min 5). The warning names the exact file(s) each worker is stuck on, so a pathologically slow file is identified rather than guessed. |
| `CODECOMPASS_THREADS` / `CODECOMPASS_SEGMENT_MB` | Indexing parallelism / per-worker segment budget. |
| `CODECOMPASS_WALK_THREADS` | Concurrent directory reads during the walk (default min(cores, 8); 1 = serial). Over a high-latency **network share** this overlaps the per-directory round-trips (SMB2 lets many be in flight), which is the main lever on a slow `update`/`index` walk; no benefit locally. |
| `CODECOMPASS_READ_BUDGET_MB` | Cap on in-flight file processing memory during a parallel build (default scales to RAM: ≈1/16th of available, clamped 256 MB–4 GB). Reservations count the real footprint (~3× file size: raw bytes + decoded UTF-16 string), so the budget genuinely fits files up to ≈budget/3 and prevents N cores each loading a big file at once when `MAX_FILE_MB` is large. A file bigger than the budget reserves it all and reads solo (blocking others until done) — no deadlock. |

Files skipped for exceeding a cap are counted and logged (not silently dropped), so the coverage gap
is always visible (`codecompass survey` / `codecompass logs`).

A repo's own `.codecompass.json` is treated as untrusted input (it arrives with a clone): its `threads`,
`walkThreads`, `maxAutoMb` and `maxFileMb` are clamped to sane ceilings. The environment variables above are
your own choice and are not clamped.

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
- Symbolic links and junctions are **not followed** (a link in a clone could otherwise pull a file from
  outside the repo into search results). The files they point to are indexed where they really live,
  if that's inside an indexed root.
- Queries shorter than 3 characters can't use the trigram index: `search_code` refuses them (they would
  read every file), and `find_references` on a 1–2 character name runs only the C# semantic pass and says so.
- Matched lines longer than 300 characters are shown as a window around the match, and control/bidi
  characters are replaced with `·` — so a minified or hostile file can't flood the agent's context or
  forge extra result lines.

### Testing

Two tiers, both driven by one script — **`check.ps1`**:

- **`./check.ps1`** — the fast xUnit suite via `dotnet test` (~15 s). Run it constantly.
  This includes **crash/corruption fuzzing**: every on-disk cache artifact is byte-flipped and
  truncated in turn, asserting the index degrades gracefully (never crashes the process) and a rebuild
  always recovers; stray orphan/temp files are ignored.
- **`./check.ps1 -Big`** — unit tests **plus** the heavy large-file scenarios, with pass/fail
  assertions: it generates a register-map header (default 0.3 GB; `-SizeGB 2` for the full run),
  confirms it streams-indexes without hanging, that a marker near EOF is found at the right line via
  the block/positional index, and that a broad query reports truncation; then re-runs the same file
  with the **network path faked on** (`CODECOMPASS_FORCE_NETWORK=1`) to confirm indexing skips the
  pre-scan and the positional search still finds the marker; then confirms the nested-template
  "pathological" files index without hanging at the default cap. Exits non-zero on any failure — this
  is the **before-you-push** check.
- **`./check.ps1 -Big -Fetch`** — also fetches the pinned real repos and runs the correctness bench
  (needs network; large).
- **`./check.ps1 -Network \\host\share\scratch`** — the real-SMB counterpart to the faked-network
  check above (run by hand; not part of the push gate). Point it at **any writable network location** —
  it generates its own corpus in a `codecompass-nettest` subdir there, indexes it over the wire,
  asserts the positional search finds the EOF marker, and removes the corpus afterward. No pre-existing
  repo or manual setup needed.

Run it automatically before every push: **`./check.ps1 -InstallHook`** (points git at the tracked
`.githooks/pre-push`, which runs `./check.ps1` - build + the xUnit suite - and aborts the push on failure).
The heavy `-Big` scenarios run on GitHub: the release workflow gates every push to `main` with
`check.ps1 -Big` before publishing. Bypass a single push's local check with `git push --no-verify`.

Big corpora are never committed (`.corpus/` is gitignored); `check.ps1 -Big` generates them on the
fly. The underlying generators can also be run directly: **`make-bigfile-corpus.ps1`** (one ~2 GB
register-map header + markers; `-Run` to index & search it), **`make-pathological-corpus.ps1`** (the
slow-to-parse shapes), and **`make-megacorpus.ps1`** (a >10 GB aggregate for scale runs).

### Per-repo config file

Every knob above also lives in an optional **`.codecompass.json`** at the repo root, so settings
travel with the repo instead of being set on every run. Precedence is **env var → config file →
default**. Fields: `maxSymbolMb`, `maxFileMb`, `maxAutoMb`, `ignore` (array of directory names), `keepDirs`
(array of default-skipped directory names to index anyway),
`threads`, `walkThreads`, `segmentMb`, `compactSegments`, `stallWarnSec`, `readBudgetMb`, `autoReconcile`,
`statusLine`, `semanticIdleMinutes`, `compileCommands` (array — extra places to find a C/C++
`compile_commands.json`; see below).

Don't hand-write it — run **`codecompass init <path>`** to drop a documented starter (every setting
commented out, so it's all defaults until you edit; `//` comments and trailing commas are allowed).
A minimal one is just:

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

### Precise C/C++ references (`compile_commands.json`)

`find_references` for C/C++ is precise only with a **compile database** — a `compile_commands.json` that
tells clang each file's real include paths and defines. Without one it falls back to best-effort flags
and may resolve little (common in vendor-toolchain firmware). CodeCompass tells you when this happens,
keyed on what the query actually did — not merely on whether a DB file exists. If a lookup's candidate
files fail to parse or reference headers that aren't in the tree, `find_references` says so and **names the
missing headers** (e.g. `C/C++ coverage INCOMPLETE: 5 unresolved #include(s): VENDOR_a.h, …`) — because a
missing header can't be fixed by any `-I` or compile DB, only by adding it to the tree. `codecompass doctor`
scans proactively too: it reports both when C/C++ sources exist with no compile DB **and** when translation
units reference unresolvable includes (`N of M … reference at least one #include not found in the tree`). So
a thin result reads as "couldn't fully run," not "no references."

To fix it, generate a compile DB (CMake `-DCMAKE_EXPORT_COMPILE_COMMANDS=ON`, or **Bear**/`compiledb` for
Make-based builds) and — if it's not in the repo root or `build/` — point at it:

```json
{ "compileCommands": ["out", "build/appB/compile_commands.json"] }
```

Each entry is a **directory** (searched for `compile_commands.json` and `build/compile_commands.json`) or
a **file**, relative to the repo root or absolute; `root` and `root/build` are always checked too. A
project that builds **multiple targets** can list several — they're **merged per source file** (their
union), so each translation unit gets its own flags. If the same file appears in more than one DB, the
first-listed wins (any valid parse resolves the symbol; we don't reproduce a specific target's object
code). One thing it does *not* do: parse the same file multiple times under different configs to capture
references inside both `#ifdef` branches. (Env: `CODECOMPASS_COMPILE_COMMANDS`, `;`-separated.)

### Tuning C/C++ find-references (speed vs. coverage)

`find_references` for C/C++ parses candidate translation units with clang, so a few knobs trade coverage
for speed and memory. CodeCompass names the relevant one in its output when a query is affected, so you
rarely set these blind.

| Env var | Effect |
|---|---|
| `CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES` | Above this many candidate C/C++ files (default **400**) the semantic parse is skipped and the fast lexical layer answers instead (disclosed as lexical). A *latency* guard for pathologically-broad symbols in generated code — parsing hundreds-to-thousands of translation units would grind for minutes and fall back to lexical anyway. Set to **0** to always attempt a full semantic parse. |
| `CODECOMPASS_CPP_SESSION_MEM_MB` | Working-set ceiling for the semantic pass across a session (default scales with RAM). Raise it to let broad queries parse more candidates before falling back to lexical. |
| `CODECOMPASS_CPP_QUERY_MEM_MB` | How much a *single* query may grow the working set before it stops parsing further candidates and discloses partial coverage. |
| `CODECOMPASS_CPP_MAX_TU_MB` | Largest C/C++ source handed to clang (default **2**). Larger candidates are searched **lexically instead** — and the answer says so, naming this knob. |
| `CODECOMPASS_CPP_PARSE_THREADS` | Translation units parsed concurrently (default: sized by free memory, at most 3). |
| `CODECOMPASS_CPP_SUBPROCESS` | `0` parses in-process instead of in a short-lived worker (the worker returns clang's native memory to the OS after each query; keep it on). |
| `CODECOMPASS_CPP_WORKER_STALL_SEC` | Hang protection for the worker (default **600**): how long it may go with **no progress** (no file finished parsing) before it's stopped. There is no total time limit - a slow but progressing parse always completes with its real answer. A stalled query reports that no C/C++ references were found by analysis; text matches are never substituted. (The old `CODECOMPASS_CPP_WORKER_TIMEOUT_SEC` is still read as a fallback.) |

`compile_commands.json` comes from the repo, so its arguments reach clang through an **allowlist**: defines,
include paths, forced includes, language/standard, target and dialect flags pass; anything that would load
code (`-Xclang -load`, `-fplugin`), remap files (`-ivfsoverlay`) or write files (`-MF`, module caches, …) is
dropped. MSVC-style `/I /D /U /FI /std:` from a `cl`/`clang-cl` database are translated.

## Staying fresh (out-of-session changes)

While Claude is running, a file watcher keeps the index current incrementally. Changes made **outside**
a session — a Perforce/git sync, a branch switch — have no watcher to catch them. On startup the MCP
server **reconciles** the loaded index against the current tree (a stat-walk vs. the last snapshot) in
the background, while the old index keeps serving, then swaps. This is automatic for **local repos
within `maxAutoMb`**; for **network shares and huge repos** it's left to a manual `codecompass update`
(a full-tree stat-walk is slow over SMB, and the watcher is unreliable there anyway). Override with
`autoReconcile` (config) / `CODECOMPASS_AUTO_RECONCILE` (env): `true` = always, `false` = never.

If the watcher loses events (its buffer overflowed during a big sync, or the share dropped it) on a network
or over-limit repo, CodeCompass does **not** start an hours-long rebuild inside the session; instead every
answer carries a note that results may be stale until you run `codecompass update`. A watcher that dies
outright is restarted automatically.

### Several sessions on one repo

Two Claude Code windows, a Claude Code and a Codex session, or a session plus a terminal `codecompass
index`/`update` can all work on the same repo safely:

- **Every write to an index holds a cross-process lock** (an OS file handle, released automatically if the
  process dies). Writers wait for each other instead of colliding; a terminal `index` that has to wait says
  `waiting for another CodeCompass process to finish writing this index...`.
- **Only one session live-watches a repo.** The others serve it read-only and **reload automatically** when
  it (or a terminal command) commits a change, so nobody serves a stale copy. When the watching session
  exits, another takes over.
- `reindex` won't park a tool call behind another process's long build — it says another process is writing
  and that the session will pick up the result when it finishes.

## Linked roots (federating external directories)

Sometimes the code you work on lives in more than one place — a wrapper project plus a shared library
checked out elsewhere, a sibling repo, or a third-party drop on another drive (`Z:\…`) that can't be
nested under your project. **Linked roots** let a project index and search those external directories
alongside its own, as one federated result set:

```
codecompass link add    <path> [project-dir]   attach an external directory (indexes it, or defers if large)
codecompass link remove <path> [project-dir]   detach it (offers to delete its index if unused)
codecompass link list   [project-dir]          show the project's linked roots + index status
```

You can do the same **from inside the agent** (Claude Code / Codex) with the **`manage_links`** MCP tool —
`manage_links` with `action: "list" | "add" | "remove" | "focus"` (and a `path` for add/remove) — so you don't
have to drop to a terminal. Either way edits the same per-project link set; a running MCP server picks up the
change on its next query. (The CLI and the tool share one implementation, so they behave identically.)

**Focus: search one linked repo at a time.** When a wrapper project links several big repos but you're
working in just one, `manage_links action=focus path="<repo>"` scopes every following search in the session
to that root. `path` matches a repo folder name, a path fragment, or an absolute path; a comma-separated list
focuses several. `action=focus` with no `path` clears it.
- Every scoped answer says what was excluded, so a narrowed search is never mistaken for "not found".
- Focusing a root that has no index yet warns immediately and on every scoped answer (it isn't searched).
- Focus filters what's *shown*, not what's *resolved*: `find_references` still binds across all roots, then
  lists only the focused roots' hits.
- It lasts for the session (cleared on restart or when the server is pointed at another project).

- **One index per root, shared across projects.** Each linked root keeps its **own** independent index
  keyed by its absolute path, so a root two projects both link is indexed **once** and reused. `link
  remove` only deletes that index if **no other project** still references it (it tells you who does).
- **Same freshness as the main repo.** When the server loads a project it federates its linked roots
  and — for each one no other live session already owns — wins a crash-proof write-ownership claim and
  **live-watches** it, so edits to a linked root are indexed as they happen, exactly like the project
  root (local *and* network). A root already owned by another session is served read-only from that
  owner's fresh index. Out-of-session changes are reconciled on load (gated for network/huge roots).
- **Cross-root code intelligence.** `find_references` and `find_callees` resolve **across** the
  boundary: a call in your project to a type defined in a linked root binds correctly (C# via Roslyn,
  C/C++ via clang — one compilation spanning all roots, not a per-root union that misses the seam).
- **Result addressing.** Hits in the project stay **repo-relative** (compact); hits in a linked root
  are shown as **absolute** paths, so they're unambiguous and directly readable.
- **Sensible guards.** You can't link a directory that's inside your project (or inside/around an
  existing link) — it's already covered. Nor a drive/share root, your user profile or AppData folder
  themselves, or anything inside a credentials directory (`.ssh`, `.aws`, `.gnupg`, `.azure`, `.kube`,
  `.docker`) — an agent shouldn't be able to put secrets into a searchable index
  (`CODECOMPASS_ALLOW_ANY_LINK=1` overrides). Links are stored **machine-local** (the paths are absolute and
  machine-specific), in the project's cache dir, not in the committed `.codecompass.json`.

`codecompass doctor "<project>"` lists every linked root, whether it exists and is indexed, and how
many other projects share it — and warns if one is missing or unindexed (so it isn't silently absent
from results).

> A `link add`/`remove` run in a terminal **while a session is open is picked up automatically** — the
> server notices `links.json` changed (a sub-millisecond check on the local cache dir, on the next
> query) and reconciles the set, keeping the roots it already serves and only adding/removing the delta.

### MCP vs CLI: which one searches more than one repo

**Only the MCP server searches across linked roots. The CLI always searches exactly one root — the path
you give it.** This is deliberate, not a gap: federation needs a *long-lived* process (it holds each
root's index open, claims write-ownership, runs the file watchers, and reconciles changes) — which is
exactly what the MCP server is, for the length of a Claude Code session. The CLI is the opposite by
design: a stateless one-shot that opens an index, answers, and exits. Making it federate would mean
running an always-on background CodeCompass service, which the tool intentionally avoids.

| | MCP server (in Claude Code) | CLI (`codecompass …`) |
|---|---|---|
| Query scope | project **+ all its linked roots**, merged into one result | the **single root** you point it at |
| `search_code` / `find_definition` / `find_references` / `find_callees` / `search_symbols` | federated across roots | one root only |
| Live watching, write-ownership, cross-root C#/C++ semantics | ✅ | — (one-shot, cold) |
| Manage links | ✅ `manage_links` tool | ✅ `link add`/`remove`/`list` |
| Build/refresh an index (`index` / `update`) | auto (per root) | ✅ per root |

So: **set federation up from either side** — the `manage_links` MCP tool (inside the agent) or the CLI
`link` commands (in a terminal); they share one implementation. Then **let the MCP tools do the actual
multi-repo searching** (that part is MCP-only — the CLI searches a single root). A CLI query on a project
that has linked roots prints a one-line reminder to that effect (so a CLI "0 results" isn't mistaken for
"not found anywhere"). You can still point the CLI directly at a linked root's own path to search just that one.

## Status line (optional)

CodeCompass can show its index state in Claude Code's status area. The MCP server publishes state to a
tiny per-repo file on every transition; the `codecompass statusline` command reads it. Wire it up in
Claude Code's `settings.json` (user or project scope):

```json
{ "statusLine": { "type": "command", "command": "codecompass statusline" } }
```

Because setting any `statusLine` command **replaces** Claude Code's built-in default entirely, the bare
command renders a **self-contained default** so you don't lose the usual info — `<model> · <cwd>` — and
appends the index state:

```
Opus 4.8 · ~/CodeCompass · CodeCompass ✓ 48,000 files
```

Other states: `… indexing 42%`, `↻ refreshing (external changes)…`, `⚠ not indexed`. The CodeCompass
part is omitted in repos it hasn't indexed (so the line degrades to just `model · cwd`).

Claude Code has a **single** status-line slot. If you already have your own status line, use `--wrap` —
it runs your command (forwarding Claude's stdin) and appends **only** the CodeCompass segment (it does
*not* add model/cwd, since your command already shows those):

```json
{ "statusLine": { "type": "command", "command": "codecompass statusline --wrap \"my-existing-statusline\"" } }
```

The command is fast, never errors out loudly, and publishing is off via `statusLine: false` (config) /
`CODECOMPASS_STATUS_LINE=0` (env).

## Diagnostics & bug reports

- **`codecompass doctor <path>`** — a one-shot health check for a repo's index: version, environment,
  config presence, index metadata (documents, segments, the version/time that built it), the cache-file
  listing (names + sizes), and explicit pass/warn checks (index builds? loads cleanly? built by the
  current version? on a network path?). Read-only — the first thing to run when search behaves oddly.
- **`codecompass cache`** — inspect/manage the per-user cache under `%LOCALAPPDATA%\CodeCompass`:
  `cache list` (each repo by real path + size + build info), `cache gc` (drop caches whose repo no
  longer exists), `cache clear <path>` / `cache clear-all`. (Each index writes a `meta.json` recording
  its repo path, so the hashed cache dirs can be listed and GC'd by real path.)
- **`codecompass report <path>`** — zip up everything a maintainer needs to diagnose a problem: the
  `doctor` snapshot, this repo's logs, and its `.codecompass.json` — and **nothing from the source
  tree**. The bundle contains file **paths/names** (from logs) and **sizes**, never file **contents** or
  index contents; only *this* repo's logs are included, not other repos'. Use `--no-logs` for a
  paths-free bundle. It prints exactly what it added and where, so you can review before sending.

## Performance

Measured on an **Intel Core i7-8700** (6 cores / 12 threads, ~2018), 32 GB RAM, Windows 11 Pro,
.NET 8, with the EcoQoS / E-core throttling opt-out in effect. A modern many-core machine builds
substantially faster.

### Per-tool latency by repo (cold vs. warm)

Every tool, timed two ways, over seven pinned public repos plus a huge generated C/C++ corpus
(local **and** over an SMB/UNC network share). Reproduce any row with
[`bench-matrix.ps1`](bench-matrix.ps1) — it fetches the public corpora on demand and prints this table.

- **cold** — a fresh CLI process per query (`codecompass search|def|symbols|refs`). What you pay the
  *first* time a tool is used after launch: process start + memory-map open + (for `find_references`)
  a cold analyzer build. The only number a one-shot CLI user ever sees.
- **warm** — the same query at steady state against one long-lived MCP server (how an agent actually
  uses it via Claude Code / Codex): the index is mapped and the semantic analyzer is resident.

Each query cell is **cold / warm**, median milliseconds. `find_references` latency depends on the
symbol, so the battery picks the **broadest** symbols in each repo (its worst case) and reports the
median and the max; **refs 1st-call** is the one-time analyzer build paid on the session's first
`find_references`.

| Repo | Lang | Files | Size | Build | search_code | find_definition | search_symbols | find_references (med) | find_references (max) | refs 1st-call |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| requests | Python | 118 | 4.9 MB | 1.3 s | 199 / 3 | 202 / 1 | 202 / 1 | 1014 / 6 | 1023 / 12 | 0.7 s |
| fmt | C++ | 207 | 3.2 MB | 0.8 s | 213 / 3 | 202 / 1 | 202 / 1 | 27830 / 18937 | 32938 / 39873 | 50 s |
| EF Core | C# | 5,002 | 82 MB | 4.2 s | 208 / 6 | 202 / 1 | 202 / 6 | 14252 / 900 | 18896 / 5644 | 13 s |
| TypeScript | TS | 72,171 | 349 MB | 18.6 s | 404 / 25 | 229 / 1 | 257 / 13 | 1484 / 96 | 1649 / 109 | 0.8 s |
| Godot | C++ | 9,937 | 196 MB | 8.7 s | 402 / 7 | 202 / 1 | 202 / 9 | 16452 / 14559 | 22382 / 21090 | 20 s |
| Roslyn | C# | 19,980 | 372 MB | 12.2 s | 245 / 18 | 202 / 2 | 238 / 32 | 34139 / 1652 | 69523 / 10316 | 31 s |
| LLVM | C/C++ | 132,213 | 1.5 GB | 41.4 s | 420 / 36 | 406 / 3 | 405 / 3 | 2228 / 700 | 104204 / 96073 | 105 s |
| generated C/C++ — **local** | C/C++ | 66,337 | 88 GB | 16m 22s | 811 / 4 | 627 / 21 | 605 / 2 | 1826 / 251 | 1888 / 256 | 1.0 s |
| generated C/C++ — **SMB/UNC** | C/C++ | 66,337 | 88 GB | 20m 38s | 2139 / 9 | 1868 / 13 | 1862 / 2 | 5858 / 344 | 6525 / 402 | 4.1 s |

The **generated C/C++** corpus is a synthetic stress tree — **66,337 files / ~88 GB**, with single
source files up to **1.4 GB** — indexed once on local disk and once over an SMB/UNC share, to show
behaviour at extreme scale and across a network.

**How to read it:**

- **`search_code` / `find_definition` / `search_symbols`** are index-backed: warm they answer in
  **~1–36 ms** on every repo (even 132 k files, even over SMB). Cold is dominated by process start +
  map-open (~0.2–2 s), which is exactly what the persistent MCP server exists to amortize away.
- **`find_references` on C#** warms up dramatically — Roslyn's workspace is built once (the 13–31 s
  *refs 1st-call*), then resident, so subsequent calls drop from tens of seconds cold to
  **sub-second–few-seconds** warm (EF Core 900 ms, Roslyn 1.7 s).
- **`find_references` on C/C++** shows little warm speedup: each call runs a **fresh clang subprocess**
  so the long-lived server never accumulates libclang's native memory (the deliberate v1.0.176
  memory-safety trade-off). So a broad C/C++ reference query costs seconds *every* time — worst on
  template-heavy trees (fmt, LLVM). The flip side is visible on the 66 k-file corpus: its broadest
  symbols trip the v1.0.210 short-circuit to lexical, so warm refs land at **251 ms (local) /
  344 ms (SMB)** — *faster than 207-file fmt*. Tune this cutoff with
  `CODECOMPASS_CPP_MAX_SEMANTIC_CANDIDATES` (see above).

**Scale check:** an aggregated **10.4 GB / ~1.1 million file** corpus indexed in **7m48s** (22 MB/s)
using **792 MB heap / 2.2 GB peak working set**, with queries still ~1 ms (p95 7.9 ms). Memory stays
bounded because the indexes are memory-mapped, not loaded into RAM. Build memory is tunable via
`CODECOMPASS_SEGMENT_MB` / `CODECOMPASS_THREADS`.

### Token savings (the whole point)

`bench eval <path>` estimates the token cost of the core task — "show me the definition of X" —
two ways over the same sampled symbols: **CodeCompass** (`find_definition` → location + the definition's
line range, small ones inlined) vs. a **grep + read-the-whole-file** baseline (what an agent does
without it). Tokens are estimated as ~chars/4; the ratio is the stable number. On this repo:

```
CodeCompass :        8,672 tokens
grep + read :      168,390 tokens
=> grep+read costs 19.4x the tokens for the same answer (95% saved).
```

It's a model, not a live-LLM trace — but it quantifies the shape of the saving (return the ~20 relevant
lines, not the whole file) and prints its assumptions so the number stays honest.

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

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🧭

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
