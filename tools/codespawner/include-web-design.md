# Include web — design

`gen --web-units N` adds an **include web**: a deep, overlapping graph of headers that a set of `.c` units
include, with the closure of every unit recorded in the manifest. It was asked for by CodeCarver, which slices a
translation unit's include closure, and it is shaped by their answers (2026-10-15/16). The web is off by default,
and a corpus without it is byte-identical to one made by 1.2.3.

`--preset huge` carries the web at full size. `--preset huge-lite` carries all of huge's shapes in about 0.5 GB:
`--scale 0.05` (1/20 of huge's source counts) with 3 giants of 32 MB, 1 big and 12 medium headers, and the web at
huge's counts with small headers.

## Layout

The web sits at the corpus root, beside the `blockN/` tree:

| Path | What it is |
|---|---|
| `inc/cfg.h` | Guarded. `CAT_(a, b) a##_##b`, `CAT(a, b) CAT_(a, b)`, and switches `CFG_0`..`CFG_15`, each 0 or 1. |
| `inc/rm_N.h` | Register maps, guarded `WEB_RM_N_H`. They are chained in segments of `--web-depth`: `rm_i.h` includes `rm_{i+1}.h` up to the end of its segment. |
| `inc/all_K.h` | Aggregators, each including a run of 4–12 segment heads. |
| `inc/tmpl_T.h` | Templates. Unguarded, with everything inside `#ifdef TNAME`. Each holds `--web-template-heads` heads `CAT(TNAME, tT…)`, some named through an alias macro, and `#undef`s its aliases at the end. |
| `inc/vx_J.h` → `inc_vnd/vx_J.h` | Four `#include_next` pairs, present when `--web-next-frac` > 0. |
| `inc_vnd/rm_N.h` | Shadows: decoys named like real register maps, containing `#error`. They are never reached, because `inc/` is searched first. |
| `web/u_N.c` | Units, the closure roots. |

The search order is `-I inc -I inc_vnd`. It is recorded as `_meta.includeDirs`, and every unit is listed in
`compile_commands.json` with that order, whatever `--compile-db` says. Most includes are quoted names found through
`-I`. A share of them are `"../inc/x.h"` (relative), `#include WEB_HDR_n_j` (computed: the macro names a quoted
path), or `<vx_J.h>` (angled).

### What a register map holds

Its lines are runs of register defines (`_ADDR`, `_MASK` and `_RST` shapes) with these blocks spread among them:

- The include of the next header in its segment.
- Optionally an include of an earlier header in its segment. This forms a **cycle**, which the guard cuts.
- Optionally an `#if CFG_k` include of a header up to two segments away (wrapping around the ends of the web),
  plus an `#if` or `#ifdef` block with defines in both branches.
- One header in ten includes a template, without TNAME. Such a header is a **carrier**: when a unit reaches it while
  TNAME is defined, the template is instantiated there.
- **Continuation:** a macro of 20–400 `\`-continued lines.
- **Comment traps:**
  - a fake `#include` inside a block comment that spans lines, and another after `//`;
  - `#define /*x*/NAME 1` and `#define NAME/*x*/1`;
  - `#undef NAME/*c*/`;
  - a define whose trailing comment spans lines.
- **Undef:** defines that are undefined and then redefined.

Each register map, template and unit has LF, CRLF (`--web-crlf-frac`) or mixed line ends, with CRLF on every
third line (`--web-mixed-eol-frac`). `cfg.h`, the aggregators and the `vx` pairs are LF.

`--web-tail` register maps are instead **tail headers** of about `--web-tail-mb` MB. They have the same blocks and
many more define lines; the knob sets the line count, so a tail comes out about 1.25× the knob (huge's 48 MB tails
are about 60 MB).

### What a unit holds

```c
#define WEB_UNIT u5
#include "cfg.h"
#include "rm_60.h"            // a window of 18-28 segment heads (--web-window), some ../inc/ or computed
...
#include "all_14.h"           // 1-2 aggregators
#if CFG_3                     // optionally, a gated include
#include "rm_412.h"
#endif
#include <vx_2.h>             // optionally, the #include_next pair
#define TNAME CAT(WEB_UNIT, r1)
#include "rm_204.h"           // a template site: deep (a chain that reaches templates) ...
#undef TNAME
#define TNAME CAT(WEB_UNIT, r2)
#include "tmpl_2.h"           // ... or direct
#undef TNAME
/* retired, kept for reference:
#include "rm_7.h"
*/
int u3_r2_t16(void);          // prototypes for instances made by earlier units
int web_u4(void);
int web_u5(void) { int s = 0; s += u5_r1_t16(); ... s += web_u4(); return s; }
```

A deep site (`--web-template-reach-frac`) includes a register map that the unit has not entered yet and whose
chain reaches carriers, and only templates not instantiated there before. Instances are therefore made
well past depth 8: about half of huge-lite's instances are deeper than 8, and the deepest is at 46. A unit compiles
cleanly with `clang -fsyntax-only` and the web's `-I` order.

## The closure (`closures` in the manifest)

These are the definitions, used by both the generator and verify:

- **`files` / `bytes`** — the distinct files the unit enters, the unit itself included. CodeCarver divides by
  `bytes`.
- **`reads` / `readBytes`** — every counted entry, the unit included: an unguarded template counts each time it
  is entered.
  - Not counted: a guard-skipped re-include. That includes a cycle back into a header still open; clang opens such a
    header and prints it under `-H`, but its guard skips the body.
  - Not counted either: an include in an inactive `#if` branch or inside a comment.
- **`depth`** — the deepest counted entry: the unit is 0 and a direct include is 1.
- **`defines`** — `#define` directives executed in active code: each counted entry runs its own, and the unit's
  defines count too.
- **`templateInstances`** — template entries made while `TNAME` is defined.

The generator computes all of these from its model of the files, not from the text it wrote. `verify` recomputes
them from the text with its own preprocessor and compares all seven numbers. When clang is installed,
`scripts\bench.ps1` checks verify's preprocessor against a real compiler: for all 40 units of a small web, the headers
clang `-H` opens give the same files, bytes, reads, readBytes and depth as the closure, once cycle re-entries are
dropped (bytes and readBytes are the sizes of the files it lists). At 1.3.2 the same check passed by hand on a web
with huge's web knobs (2000-line headers, 48 MB tails): its first 20 units, its deepest (72) and its largest read
(889 MB). Neither verify nor the tests run clang.

Every closure is at most 190 deep (gen refuses a web whose closures would go deeper), under the 200 nested includes
gcc and clang allow. `--web-depth` is capped at 150 for that reason; a deep site's chain and the aggregators add
to it.

## Template instances (symbols)

Each instance is a symbol named after its template's primary head, for example `u5_r1_t16`:

- `def` — the unit's `#include` line that led to it;
- `templateHead` — `inc/tmpl_16.h:<line>` of the primary head;
- `includedVia` — the template header, `inc/tmpl_16.h`;
- `defNotLiteral: true` (since 1.3.2) — the def is not the name's text, but the refs are real call sites an indexer
  should resolve. Unlike `expectedMiss`, it is not part of `truthSha`;
- `refs` — call sites: the unit's own call, and calls from later units through prototypes.

The name is made by token pasting, so it never appears at the def. Verify therefore checks that the def is an
`#include` line, and then requires every instance its walk finds to be declared exactly once with the same def,
`templateHead` and `includedVia`, and every declared instance to be found.

## Verify's preprocessor

`IncludeClosure` (in `src/CodeSpawner/Verify/`) is a deliberately small preprocessor. It handles exactly what the
web uses:

- guards: the first directive is `#ifndef G`, the next is `#define G`, and the matching `#endif` is the last;
- `#if` / `#ifdef` / `#ifndef` / `#elif` / `#else` / `#endif`, with `defined`, `!`, `&&`, `||`, `==`, `!=`,
  integers and object-like macros;
- comments, including a block comment whose newlines are swallowed and a `//` comment continued by `\`;
- `\` continuations with LF or CRLF;
- quoted, angled and computed includes, where a quoted include tries the includer's directory first, and a
  computed include's macro may be defined in any file the closure reaches;
- `#pragma once`, `#error` (an error when active) and the compilers' limit of 200 nested includes;
- `#if` integers in decimal, octal, hex and binary, with `u`/`l` suffixes (anything else is an error);
- `#include_next`, which resumes the search after the directory where the current file was found;
- containment: an include that leaves the corpus, or passes through a link, fails.

Function-like macros are never expanded, except `CAT(a, b)`, which pastes `a_b`. In headers it tracks only the names
that conditions, computed includes and `TNAME` need, and the names their definitions use (every `#define` still
counts). Each file is parsed once into
events, so a closure costs its directives, not its bytes: huge-lite's 3000 closures verify in about 20 s.

At huge's settings the web is 1.65 GB of headers, with closures of about 350 MB at p50, 645 MB at p95 and
850 MB at most per unit, and verify still recomputes all 3000 in about 11 s.

## Knobs

All of them are never scaled, and refused unless `--web-units` is given. `--lang cpp` and `--manifest false` are
refused with the web, and so is a template knob with `--web-templates 0` or `--web-tail-mb` without `--web-tail`.

`gen --from-profile ... --with-oracle` takes them too (since 1.3.1): the web is written at the root beside the
profile's `d*` tree, which stays byte-identical, and its truth is added to the oracle manifest. This is how the
real-shape huge keeper carries the web. A `--preset` cannot be combined with `--from-profile`, so the knobs are spelled
out; `huge`'s are `--web-units 3000 --web-headers 3000 --web-depth 15 --web-window 18-28 --web-aggregators 40
--web-templates 60 --web-template-sites 4 --web-tail 24 --web-header-lines 2000 --web-tail-mb 48`.

| Knob | Default | Shape |
|---|---|---|
| `--web-units` | 0 | Units, and so closures. |
| `--web-headers` | 600 | Register maps. |
| `--web-depth` | 12 | Segment length, i.e. chain depth (at most 150). |
| `--web-window` | `6-12` | Segment heads per unit (`N` or `MIN-MAX`). |
| `--web-aggregators` | 16 | Aggregator headers. |
| `--web-header-lines` | 300 | Lines per register map (each one is ±50%). |
| `--web-tail` / `--web-tail-mb` | 0 / 32 | Tail headers and their size (each comes out about 1.25× the MB). |
| `--web-templates` / `--web-template-sites` / `--web-template-heads` | 24 / 3 / 12 | Templates, TNAME sites per unit, heads per template. |
| `--web-template-reach-frac` | 0.4 | Share of sites that go deep. |
| `--web-template-hidden-frac` | 0.25 | Share of heads named through an alias. |
| `--web-cycle-frac` | 0.2 | Headers with a back-include. |
| `--web-ifdef-frac` | 0.15 | Headers and units with a gated include. |
| `--web-continuation-frac` / `--web-comment-frac` / `--web-undef-frac` | 0.2 / 0.2 / 0.1 | The text traps. |
| `--web-computed-frac` / `--web-relative-frac` | 0.05 / 0.05 | Include spellings. |
| `--web-next-frac` / `--web-shadow` | 0.05 / 4 | `#include_next` and shadow decoys. |
| `--web-crlf-frac` / `--web-mixed-eol-frac` | 0.05 / 0.02 | Line ends of register maps, templates and units. Their sum must be ≤ 1. |

`mutate` never edits the web's directories, because the closure truth names their bytes. `--three-way` rewrites
each variant's `compile_commands.json` to the variant's own paths (since 1.3.2) and lists it under the deltas'
`_meta.bookkeeping`. `verify` checks the db: every unit listed with the web's `-I` order, and a variant whose db
still names its base is a FAIL. A db naming any other tree (a corpus moved or reached by another path) is a note.
