# Synthetic Firmware-Corpus Generator

`make-firmware-corpus.ps1` — a deterministic generator that fabricates a synthetic C-firmware source
tree matching the **measured structural shape** of a large proprietary firmware repository, for use as
**reusable test/regression infrastructure**. Windows PowerShell 5.1 compatible; no external dependencies
to generate (the optional `-Run`/`-Verify` steps additionally need the CodeCompass Release CLI).
Nothing proprietary is reproduced: only file counts, size distributions, and line *shapes* measured from
filesystem metadata.

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
| `-MacroDensity` | `#define`s per header, **independent of file size** — symbol-dense headers (extraction / memory) |
| `-GiantHeaders` / `-BigHeaders` / `-MedHeaders` / `-MaxHeaderMB` | the byte pathology (giants fill to `-MaxHeaderMB`; scale reduces counts, not sizes) |
| `-BlobFiles` | high-byte, zero-symbol data blobs — parser cost / skip heuristics |
| `-CompileDb none\|partial\|full` | presence of build metadata (`compile_commands.json`); `none` is the firmware norm |
| `-BuildOutput` | `.o/.elf/.lst/.a/.bak` committed **beside** sources — exclusion rules & lexical noise |
| `-TinyFiles` | thousands of ~1 KB files — per-file overhead / network stat pressure |
| `-Dirs` / `-Depth` | wide, deep trees — path handling / long paths |
| `-CrossRefs` (built-in) | cross-file cross-directory call edges — reference correctness |
| `-LinkedRoots` | output split across N trees — multi-root / federation |
| `-UnresolvedIncludes` | N TUs `#include` a vendor header absent from the tree, holding a reference gated behind a macro only that header defines — uses a compiler would drop, which a name search must still list |
| `-Scale`, `-Seed` | overall count multiplier; deterministic replay |

## The correctness oracle

Each generated `.c` defines `func_i` and calls `func_{i-1}` (usually in another directory). The generator
records the exact definition line of each `func_i` and the exact line where it is called, and writes them
to `<out>-manifest.json`. With `-Verify`, it indexes the corpus, runs `find_references` for a sample of
symbols, and asserts the tool's results match the manifest. This is the piece worth stealing for any tool
that needs *ground-truth* correctness testing.

With `-UnresolvedIncludes N`, the manifest also carries a **gated** set: `vendor_gated`'s references live
behind a macro only a missing header defines, so `refs` is empty and `unreachableRefs` lists those sites.
CodeCompass's C/C++ references are a name search, so `-Verify` asserts (a) those sites ARE listed — a use of
the name is still a use — and (b) the answer says C/C++ references are matched by name.

## Usage

```powershell
# Fast correctness-oracle smoke (no giant headers): seconds.
./make-firmware-corpus.ps1 -Out .corpus/_fw -Scale 0.01 -GiantHeaders 0 -BigHeaders 0 -MedHeaders 0 -Verify

# Pure memory-axis repro: one giant header + a .c that includes it.
./make-firmware-corpus.ps1 -Out .corpus/_fw -GiantHeaders 1 -CFiles 5 -OrdinaryHeaders 0 -TinyFiles 0 -Run

# Density-only stressor: 1M macros in a small file (memory without the bytes).
./make-firmware-corpus.ps1 -Out .corpus/_fw -GiantHeaders 1 -MacroDensity 1000000 -MaxHeaderMB 500

# Suggested tiers:
#   CI      ~1/100 counts, keep >=1 pathology header
#   nightly full scale
#   matrix  vary ONE knob against a fixed baseline for attribution
```

Generation needs only PowerShell. `-Run` (index + peak-RSS measurement) and `-Verify` (the oracle) need
the CodeCompass Release CLI at `src/CodeCompass.Cli/bin/Release/net10.0/CodeCompass.Cli.exe`.

## Performance

The register-header emitter writes ~5 MB batches from a `StringBuilder` in one call each (vs. per-line
writes), so a **111 MB / ~2M-`#define` header generates in ~3 s**. A full ~90 GB nightly is on the order
of tens of minutes, not hours.

## Notes

- **Self-contained**: single PowerShell 5.1 script, no modules. Generation has zero external deps.
- **The block-batched large-file emitter** (`New-RegHeader`) is a reusable recipe for generating multi-GB
  text files quickly from PowerShell.
- **PowerShell 5.1 gotchas** (baked into the working script): variable names are
  case-insensitive (a `$Dirs` param collides with a `$dirs` local); an inline `if` can't be used as a
  command argument; native-exe stderr faults under `$ErrorActionPreference='Stop'` (use `Continue` and
  check `$LASTEXITCODE`).
