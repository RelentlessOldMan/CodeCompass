# Synthetic Firmware-Corpus Generator

`make-firmware-corpus.ps1` — a deterministic generator that fabricates a synthetic C-firmware source
tree matching the **measured structural shape** of a large proprietary firmware repository, for use as
**reusable test/regression infrastructure**. Windows PowerShell 5.1 compatible; no external dependencies
to generate (the optional `-Run`/`-Verify` steps additionally need the CodeCompass Release CLI).

> Handoff copy. Authored for the **CodeCompass** project (a local code-search/indexing engine). Placed
> here so another project ("Code Carver") can evaluate whether any of it — the generator, the size
> distribution model, the block-batched large-file emitter, or especially the **correctness-oracle
> pattern** — is reusable. Nothing proprietary is reproduced: only file counts, size distributions, and
> line *shapes* measured from filesystem metadata.

---

## Why it exists

A real firmware repo surfaced defects that came from its **structure**, not its raw size. The defining
fact: **~90% of the bytes live in ~2% of the files** — a few thousand machine-generated hardware
register-map headers (~110 MB each, ~1 million `#define`s), amid tens of thousands of tiny ordinary
files. A generator that spreads bytes evenly reproduces *none* of the failures. **The bimodality is the
test.**

Every defect the real repo exposed traced to a *different* structural property, so the generator makes
each property an **independent, dial-able knob**. Dial one axis against a fixed baseline and a regression
bisect names the subsystem, instead of "something broke in a 90 GB blob."

## Design principles

1. **Scale by COUNTS, never per-file size.** The 100 MB-plus headers are the pathology; even `-Scale 0.01`
   keeps at least one. Capping file size to make a "small" corpus tests nothing.
2. **Deterministic** given `-Seed`, so any failure replays exactly.
3. **Independent knobs**, each mapping to an observed failure mode (below).
4. **Ground-truth correctness oracle**: because the generator knows what it emitted, it writes the exact
   definition/reference sites of every generated symbol, turning "find-references returned 4 hits" into a
   pass/fail assertion. On a real proprietary tree nobody knows the true answer — which is exactly how a
   correctness gap can ship unnoticed. A synthetic corpus is the only way to get ground truth.

## The knobs (each = an observed failure class)

| Knob | Property it stresses |
|---|---|
| `-MacroDensity` | `#define`s per header, **independent of file size** — preprocessor/macro-table memory |
| `-GiantHeaders` / `-BigHeaders` / `-MedHeaders` / `-MaxHeaderMB` | the byte pathology (giants fill to `-MaxHeaderMB`; scale reduces counts, not sizes) |
| `-BlobFiles` | high-byte, zero-symbol data blobs — parser cost / skip heuristics |
| `-CompileDb none\|partial\|full` | presence of build metadata (`compile_commands.json`); `none` is the firmware norm |
| `-BuildOutput` | `.o/.elf/.lst/.a/.bak` committed **beside** sources — exclusion rules & lexical noise |
| `-TinyFiles` | thousands of ~1 KB files — per-file overhead / network stat pressure |
| `-Dirs` / `-Depth` | wide, deep trees — path handling / long paths |
| `-CrossRefs` (built-in) | cross-file cross-directory call edges — reference correctness |
| `-LinkedRoots` | output split across N trees — multi-root / federation |
| `-Scale`, `-Seed` | overall count multiplier; deterministic replay |

## The correctness oracle

Each generated `.c` defines `func_i` and calls `func_{i-1}` (usually in another directory). The generator
records the exact definition line of each `func_i` and the exact line where it is called, and writes them
to `<out>-manifest.json`. With `-Verify`, it indexes the corpus, runs `find_references` for a sample of
symbols, and asserts the tool's results match the manifest. This is the piece worth stealing for any tool
that needs *ground-truth* correctness testing.

## Usage

```powershell
# Fast correctness-oracle smoke (no giant headers): seconds.
./make-firmware-corpus.ps1 -Out .corpus/_fw -Scale 0.01 -GiantHeaders 0 -BigHeaders 0 -MedHeaders 0 -Verify

# Pure memory-axis repro: one giant header + a .c that includes it.
./make-firmware-corpus.ps1 -Out .corpus/_fw -GiantHeaders 1 -CFiles 5 -OrdinaryHeaders 0 -TinyFiles 0 -Run

# Density-only stressor: 1M macros in a small file (memory without the bytes).
./make-firmware-corpus.ps1 -Out .corpus/_fw -GiantHeaders 1 -MacroDensity 1000000 -MaxHeaderMB 500

# Suggested tiers (per the source spec):
#   CI      ~1/100 counts, keep >=1 pathology header
#   nightly full scale
#   matrix  vary ONE knob against a fixed baseline for attribution
```

Generation needs only PowerShell. `-Run` (index + peak-RSS measurement) and `-Verify` (the oracle) need
the CodeCompass Release CLI at `src/CodeCompass.Cli/bin/Release/net8.0/CodeCompass.Cli.exe`.

## Performance

The register-header emitter writes ~5 MB batches from a `StringBuilder` in one call each (vs. per-line
writes), so a **111 MB / ~2M-`#define` header generates in ~3 s**. A full ~90 GB nightly is on the order
of tens of minutes, not hours.

## What it has already produced (CodeCompass)

- **Root-caused a memory blowup cheaply.** One `.c` including a 1M-`#define` header cost 1.6 GB / 10 s for
  a single `find_references` — a per-translation-unit macro-table problem, confirmed in seconds without a
  90 GB corpus.
- **Validated the fix**: 1.6 GB → **~150–400 MB**, references still resolve.
- **Correctness oracle: 20/20** cross-file references resolved with no compile database — proving a
  redesign was *correct*, not merely fast.
- **Caught a real bug on its first run**: the CLI reference search was reporting `.lst`/`.bak` build-output
  as references (a filter the GUI path had but the CLI lacked) — fixed.

## Reuse notes (for evaluation elsewhere)

- **Self-contained**: single PowerShell 5.1 script, no modules. Generation has zero external deps.
- **The oracle pattern generalizes**: emit-with-ground-truth-manifest + assert-tool-output-against-it works
  for any analyzer/indexer/search tool, not just C/C++.
- **The block-batched large-file emitter** (`New-RegHeader`) is a reusable recipe for generating multi-GB
  text files quickly from PowerShell.
- **CodeCompass-specific bits** to swap out if reused: the `-Run`/`-Verify` steps shell out to the
  CodeCompass CLI (`index`, `refs`); the register-map line *shapes* are C-firmware-flavored. The size
  distribution model and knob structure are domain-agnostic.
- **PowerShell 5.1 gotchas encountered** (baked into the working script): variable names are
  case-insensitive (a `$Dirs` param collides with a `$dirs` local); an inline `if` can't be used as a
  command argument; native-exe stderr faults under `$ErrorActionPreference='Stop'` (use `Continue` and
  check `$LASTEXITCODE`).
