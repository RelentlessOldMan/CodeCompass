# diff-delta — design doc (LOCKED contract for the large-repo diff tool)

**Status:** contract **locked** with the CodeDiffer session in the `code-spawner` chat channel (2026-10-02,
`stop_reason: agreed`). This is the ground-truth shape CodeDiffer's 2-/3-way diff tool asserts against. It
extends bulk-mode `mutate` (see [mutate-design.md](mutate-design.md) → "Bulk mode (diff-oracle)"); the
file-level truth there is step 0, this doc freezes the hunk/rename/3-way truth that was deferred.

## Consumer & contract basis

- **Consumer:** CodeDiffer — a diff tool for very large repos. Operates at **LINE/HUNK** granularity, with a
  **byte/metadata summary** track for binary files. Structural/syntactic diff is a deferred v2 that would
  reuse the existing symbol/edges truth — **not** in scope here.
- **Patch-applicable:** CodeDiffer's output is unified diffs verified against `git apply`/`patch`. So every
  hunk below is emitted in **1-based unified-diff coordinates** and must materialize to an applyable patch.
- **Honesty contract (CodeDiffer's headline bug class):** never report "modified" with no reason / empty
  diff. That is why the **`reason` label is itself ground truth** — a reason-flip must be a test failure, so
  `reason` is digested (below), not just the hunks.

## Command surface (extends bulk mode)

```
codespawner mutate --corpus <dir> --target <source|headers|giant|all>
                   [--files-changed N] [--edit-density f] [--giant-min-mb N]
                   [--edit-kind <kind>] [--seed S]
# 3-way (joint generator; step 4):
codespawner mutate --corpus <dir> --target ... --three-way --overlap-fraction f --seed S
```

- **`--edit-kind`** selects the *mechanism* of the edit; it maps onto a *classification* (`reason`) that
  CodeDiffer asserts against. Locked **reason tokens**: `content | eol | whitespace | encoding | binary |
  metadata`. `line-insert` / `line-delete` are edit-kinds that classify as **`reason=content`** with
  `op=insert|delete` — they exist to exercise the diff tool's line-renumber path.
- **`--three-way` + `--overlap-fraction f`** — the only 3-way dial (step 4); see below.

## Reason classes (step 2 — CodeDiffer's top-value ask)

Each changed file in the delta carries its `reason`. One small carve-style labeled fixture ships per class.

| reason | mechanism | bytes | textual hunks | notes |
|---|---|---|---|---|
| `content` | append marker / add / remove lines | differ | yes (replace, or insert/delete for line-insert/line-delete) | the existing bulk edit; line-insert/line-delete exercise renumbering |
| `eol` | flip LF↔CRLF | differ | **zero** | the "modified-with-empty-diff" honesty case |
| `whitespace` | reindent / trailing spaces | differ | **yes** (all changes whitespace-only) | feeds the ws-insensitive collapse path |
| `encoding` | UTF-8↔UTF-16, add/remove BOM | differ | **zero** (decoded text identical) | tests decode-before-diff |
| `binary` | edit bytes in a blob file | differ | **zero** | metadata-only record (shas+sizes), no byte-range truth in v1. The new bytes always carry a NUL in the first 8 KB and never start with a wide BOM (FF FE / FE FF / 00 00 FE FF), so a NUL-sniffing diff classifies them binary |
| `metadata` | synthetic mode/metadata change | **identical** (`oldSha==newSha`) | **zero** | fixture-sized; NTFS has no real POSIX mode so this is synthetic. Generic field, mode is just the first instance. Proves classify-as-`metadata_only` + the opt-in metadata-diff path |
| `mixed` | one kind per file, cycling the six reasons above | varies per file | varies per file | NOT a reason value — a `--edit-kind mixed` knob that assigns a deterministic per-file kind (`content,eol,whitespace,encoding,binary,metadata` by index). ≥6 **content-bearing** files ⇒ ONE delta exercises every classifier branch (a kind that no-ops on a degenerate file — e.g. `eol` on a file with no line terminators — is honestly dropped by the no-change guard, so that reason would be absent). Renames stay a separate pass (`--edit-kind rename`). |

## Delta schema — modified-files + hunks

The diff delta's `_meta` (in a sharded delta, the index's):

```json
"_meta": {
  "manifestVersion": 1, "deltaKind": "diff", "editKind": "content", "baseSeed": 1337, "editSeed": 7,
  "target": "source", "editDensity": 0.05, "baseManifestSha": "…", "prevTruthSha": "…", "diffTruthSha": "…"
}
```

`editKind`, `target` and `editDensity` echo the run's `--edit-kind`, `--target` and `--edit-density`;
`baseManifestSha` and `prevTruthSha` are as in a symbol delta ([mutate-design.md](mutate-design.md)), and
`diffTruthSha` is the digest below. The index of a sharded delta adds `shardSize`. Symbol deltas carry no
`editKind`/`target`/`editDensity`/`diffTruthSha`; the 3-way conflict file's `_meta` is shown under step 4.

Per changed file, in `fileOps.modified` (now objects, not bare paths):

```json
{
  "path": "blockC/.../src_3.c",
  "reason": "content",
  "oldSha": "<sha256 lowercase hex of raw old bytes>",
  "newSha": "<sha256 lowercase hex of raw new bytes>",
  "oldSize": 40960,
  "newSize": 41008,
  "hunks": [
    { "op": "replace", "oldStart": 120, "oldLines": 1, "newStart": 120, "newLines": 1 }
  ]
}
```

- **op** ∈ `insert | delete | replace`. insert ⇒ `oldLines==0`; delete ⇒ `newLines==0`; replace ⇒ both ≥1.
- **Line numbers (pinned):** all 1-based. `oldStart` = the first old line the hunk covers; for an **insert**
  (`oldLines==0`) it is the number of old lines BEFORE the insertion point (git-style `-N,0`; `0` = top of
  file). `newStart` = the number of new lines before the hunk **+ 1** for every op — for a **delete**
  (`newLines==0`) it is the new line the deleted region would have begun at. A file is reconstructable from its
  old text + hunks: walk them in order, copy old lines up to `oldStart` (insert: up to and including it), emit
  the `newLines` new lines, skip `oldLines` old lines. Hunks are listed in file order (ascending `oldStart`,
  then `newStart`); before 1.1.11 a 3-way variant's add/add insert could follow a later hunk.
- **Coalescing:** contiguous touched lines collapse into ONE replace hunk (`oldLines==newLines==run`), proper
  unified semantics — not N 1-line hunks.
- **oldSha/newSha** = SHA-256 of the raw file bytes, **lowercase hex** (= CodeDiffer's size+hash prefilter
  hash). **oldSize/newSize** = decimal bytes, feeds the size prefilter + the binary summary.
- `eol`/`encoding`/`metadata`/`binary` records carry shas+sizes with an **empty** `hunks` list.

### Giant files — compact run-rule (the scale decision)

An explicit hunk list for `--edit-density 0.05` on a >1 GB header is hundreds of thousands of entries. For
files ≥ `--giant-min-mb`, selection switches from per-line-random to a **deterministic STRIDE** so a single
run-rule describes it exactly:

```json
{ "op": "replace", "kind": "run", "stride": 20, "rangeStart": 1, "rangeEnd": 5000000, "perHunk": 1 }
```

**Byte-exact expansion (pinned, both sides mirror):** touched lines =
`{ rangeStart + k*stride : k=0,1,… while ≤ rangeEnd }`, `rangeStart`/`rangeEnd` **1-based INCLUSIVE**, each a
`perHunk`-length replace. `perHunk=1` ⇒ one 1-line replace every `stride` lines. `density → stride` (0.05 ⇒
stride 20). Tiny delta, fully reconstructable, **no coupling to CodeSpawner's RNG**. Normal files stay
explicit-hunks; the byte track is reserved for actual binary files.

**Single-locality variant (`--giant-edit single`):** instead of the strided run-rule, make ONE localized
one-line insert at the giant's midpoint, emitted as a SINGLE explicit insert hunk
(`{op:insert, oldStart:mid, oldLines:0, newStart:mid+1, newLines:1}`). This is the content-defined-chunker
locality case — a tiny edit in a multi-GB file that a block-indexing diff must re-diff in ~one block of
thousands. Terminator-faithful + streamed in two passes (count, then rewrite), so the giant never lands on the
heap. Default stays `strided`. If the anchor is the file's last line and it has no terminator (a one-line
giant), the insert gives that line a `\n` first, so the line itself changes: the hunk is then ONE replace,
`{op:replace, oldStart:mid, oldLines:1, newStart:mid, newLines:2}`, which is what a line diff reports (since
1.1.11; before, the inserted text was glued onto the unterminated line under an insert hunk).

## Rename / move (step 3)

```json
"renamed": [ { "from": "…/src_9.c", "to": "…/moved_9.c", "similarityMilli": 1000 } ]
```

- Both **pure renames** (`similarityMilli==1000`, identical bytes, no hunks) AND **rename+edit**
  (`similarityMilli<1000`); rename+edit hunks live in `fileOps.modified` keyed by the **`to`** path (so the
  `renamed` section stays flat).
- **Similarity metric (pinned):** `commonLines / max(oldLineCount, newLineCount)` over **EOL-normalized**
  lines, `commonLines` = multiset intersection; encoded as `similarityMilli` in integer thousandths
  (dodges float-format ambiguity, same spirit as bools-as-0/1). Matches CodeDiffer's own engine so the
  threshold test is apples-to-apples. Pinned precisely (1.1.13, agreed with CodeDiffer 2026-10-14):
  - Lines are counted on the **decoded text**, never raw bytes: a BOM is honoured (so UTF-16 is decoded
    first), otherwise UTF-8. CRLF and a lone CR count as LF; a final terminator does not start another line.
  - Rounding is **half-up in integers**: `similarityMilli = (1000·commonLines + m/2) / m` with integer
    division and `m = max(oldLineCount, newLineCount)` (so 9/16 = 562.5 records 563).
  - A file that isn't text (binary) gets no similarity: its rename is always pure (1000, identical bytes).
- **Graded:** emit renames near ~0.9 / ~0.6 / ~0.3 to probe CodeDiffer's ~0.5 default threshold boundary.
- **Decoys:** scatter near-duplicate **non-renamed** files (dup `nearVariants`) so CodeDiffer scores false
  **positives**, not just recall. Truth = the `{from,to}` set ⇒ a precision/recall oracle (a missed rename
  shows as delete+add; a bad one as a spurious rename).

## 3-way (step 4 — joint generator)

From base B, two variants V1, V2 via two edit-seeds. **`--overlap-fraction f`** makes V2's line selection
aware of V1's: V1's lines are picked, then V2's so fraction `f` of them land on V1's touched lines →
graded **0%** (all clean-merge) / **50%** / **100%** (all conflict). Emits two deltas (B→V1, B→V2) + a
**separate conflict artifact** with its own `conflictTruthSha` (kept off the 2-way `diffTruthSha`, exactly as
`indirectTruthSha` is kept off `prevTruthSha`):

```json
{
  "_meta": {
    "manifestVersion": 1, "deltaKind": "conflict-3way", "baseSeed": 1337, "editSeed": 7,
    "overlapFraction": 0.5, "baseManifestSha": "…", "v1Tree": "B_v1", "v2Tree": "B_v2",
    "conflictTruthSha": "…"
  },
  "conflicts": [
    { "path": "…", "baseStart": 121, "baseLines": 1,
      "v1": { "op": "replace", "newStart": 121, "newLines": 1 },
      "v2": { "op": "replace", "newStart": 121, "newLines": 1 } }
  ],
  "mergedClean": [
    { "path": "…", "side": "v1", "op": "replace", "oldStart": 125, "oldLines": 1, "newStart": 125, "newLines": 1 }
  ]
}
```

Each conflict's `v1`/`v2` carry only the op and the region's place in that variant tree (base coordinates are
the shared `baseStart`/`baseLines`); each `mergedClean` record is flat and names the one `side` whose tree holds
the text. These are exactly the fields of the two `conflictTruthSha` sections below.

- A region **conflicts** iff both sides modify overlapping base line ranges with **differing** content.
  Non-overlapping → clean. **Identical** change both sides → clean (same result).
- `mergedClean` = the expected merged-result hunk set for clean regions, so CodeDiffer asserts the **full
  merge**, not only the conflict set.
- **identical-overlap-clean** (both sides make the byte-identical change on an overlapping range → diff3
  resolves clean) is a **single labeled fixture**, not a dial — the only 3-way dial is `--overlap-fraction`.
- `conflictTruthSha` sections `[conflicts-3way, merged-clean]`: field orders pinned when step 4 is built;
  canonical form principles identical to `diffTruthSha` below.

## Integrity digest — `_meta.diffTruthSha`

Reuses the **exact** canonical form locked for `indirectTruthSha` (no new dialect — same `IndirectDigest`
machinery):

- **Ordinal** (byte-wise UTF-8) sort everywhere; separators are literal bytes **US `0x1F`** (field) /
  **RS `0x1E`** (record) / **GS `0x1D`** (section); bools as `0/1`; **dedup-then-sort** each section;
  sha256 **lowercase hex**; **ALL sections always emitted**: an empty section contributes zero bytes, but the
  GS separators between sections are still written (there is no section header).
- File-level metadata (`reason`, shas, sizes) is digested — a reason-flip or sha-flip **trips the digest**,
  not just the hunks.

**Four sections, this fixed order:**

| # | section | fields (US-joined, in order) |
|---|---|---|
| 1 | modified-files | `path · reason · oldSha · newSha · oldSize · newSize` |
| 2 | hunks-explicit | `path · op · oldStart · oldLines · newStart · newLines` (op ∈ insert\|delete\|replace) |
| 3 | hunks-run | `path · op · stride · rangeStart · rangeEnd · perHunk` |
| 4 | renames | `from · to · similarityMilli` |

A `diffTruthSha` golden vector is frozen into `digest-selftest` so CodeDiffer's verify-adapter reproduces it
independently before step (1) is called locked — the same bar CodeCarver/CodeCompass held CodeSpawner to on
`indirectTruthSha`.

**Golden vector (frozen 2026-10-02, `digest-selftest` + `DiffDigestTests`):** three modified files inserted
out of order (proves the ordinal sort) — `z/last.c` (reason content, o1/n1, 100→110, one `replace 5,2`),
`a/first.c` (o2/n2, 200→205, two hunks `replace 1,1` + `replace 9,3`), `big.h` (o3/n3, 1048576→1050000, a
run-rule `replace stride=20 range=1..5000 perHunk=1`), and **empty renames**:
**`diffTruthSha = 66c7e62566ee105e63dce7e770d47e1a9fbe71b86d50249e209f5bf41f03d542`** (sha256, lowercase hex).
Reproduce this exact hash before wiring further.

**Step 1 status:** IMPLEMENTED. Bulk `mutate` emits the `deltaKind:"diff"` delta above (modified-files with
reason/shas/sizes + coalesced explicit hunks, run-rule for giant files, empty `renamed`, `_meta.diffTruthSha`).

**Step 2 status:** IMPLEMENTED. `--edit-kind <content|line-insert|line-delete|whitespace|eol|encoding|binary|
metadata>` selects the mechanism; each maps to its locked `reason`. `line-insert`/`line-delete` emit
`insert`/`delete` hunks that renumber following lines (reason stays `content`); `whitespace` emits replace
hunks (reason `whitespace`); `eol`/`encoding`/`binary` emit shas+sizes with ZERO hunks; `metadata` is
content-identical (`oldSha==newSha`) with a synthetic `metadata:{field,old,new}` (not digested — the locked
modified-files section is exactly the six fields above). The honesty guard drops any file whose bytes did not
actually change, and the content rewrite preserves LF/CRLF + a missing final newline so a no-op is a true
no-op.

**Byte-faithful text edits (after 1.1.3).** Every line-level kind (`content`, `whitespace`, `line-insert`,
`line-delete`, `eol`, rename+edit, the 3-way variants, giant strided/single) writes a file back in the
encoding it was read in — UTF-16LE/BE stay UTF-16, a UTF-8 or UTF-16 BOM stays — so the only bytes that
change are the edited lines (or, for `eol`, the terminators). Decoding is strict. A file that cannot be
round-tripped exactly is **not editable text** and those kinds leave it untouched and unrecorded. That covers
invalid UTF-8, NUL bytes without a UTF-16 BOM (binary), and a UTF-32 BOM. `encoding` likewise skips such files,
since "decoded text identical" must hold; under rename it becomes a pure rename; in 3-way neither variant
edits it. `binary` and `metadata` apply to any file. The generator's root `compile_commands.json` (absolute
paths) and stray `*.mut.tmp` files are never selected as targets.

**Step 3 status:** IMPLEMENTED. `--edit-kind rename` renames chosen files with GRADED `similarityMilli` (a
rotating band {1000,900,600,300}; the emitted value is the REALIZED overlap `round((L-changed)/L*1000)`, not
the target, so tiny files honestly report low similarity). Pure renames (1000) are identical bytes with no
`modified` record; rename+edit emit their hunks in `modified` keyed by the `to` path (reason content), so
`renamed` stays flat. `--decoy-fraction f` emits near-duplicate ADDs (`*_dup*`, original kept) into
`fileOps.added` as rename false-positive traps — `added` is NOT digested (not a locked digest section), it is
scoring truth for CodeDiffer: the `{from,to}` set is the recall oracle, the decoys the precision oracle.
**Rename margin (1.1.12):** every decoy left in `added` scores at most `similarityMilli − 20` against every
recorded rename's `from` (pinned metric, both directions of pairing); a decoy closer than that is deleted and
left out of `added`. Every decoy differs from its source (at least one marked line; a non-text or empty file
gets no decoy, 1.1.13). Renames below 20 milli, non-text bases, and renames past the check's line budget (32M
indexed lines, a stderr warning says how many) are not checked. Another rename's `to` can't be dropped, so a
target set holding near-copies of each other can still have renames that cross-match; mutate warns on stderr
with the count. That happens with the default `--source-bodies same` sources, and with `--target all`, whose
`.bak`/`.o`/`.lst` build outputs are copies of each other. The best-match guarantee holds for rename fixtures
generated with `gen --source-bodies varied` and mutated with `--target source`.
**Step 4 status:** IMPLEMENTED. `--three-way --overlap-fraction f` leaves base B pristine and emits two
mutated sibling trees `B_v1/` + `B_v2/`, their standard `deltaKind:"diff"` deltas `B-delta-v1.json` /
`B-delta-v2.json`, and `B-conflict.json` (`deltaKind:"conflict-3way"`, `_meta.conflictTruthSha`). All edits
land on **odd base lines** so every even line is a stable anchor (the stable-separator guarantee ⇒ each
conflict region is one base line, region-level ≡ line-level); `f` = fraction of V2's edited lines that coincide
with V1's. `conflictTruthSha` canonical form (identical discipline to `diffTruthSha`), two sections in this
fixed order:

| # | section | fields (US-joined) |
|---|---|---|
| 1 | conflicts-3way | `path · baseStart · baseLines · v1Op · v1NewStart · v1NewLines · v2Op · v2NewStart · v2NewLines` |
| 2 | merged-clean | `path · side · op · oldStart · oldLines · newStart · newLines` (side ∈ v1\|v2) |

Within a file, conflicts are listed by (`baseStart`, `baseLines`) and clean merges by (`oldStart`,
`oldLines`) (since 1.1.11; the digest sorts its own records, so `conflictTruthSha` is unchanged).

`new*` coords reference the respective variant tree (`B_v1`/`B_v2`), `base*`/`old*` reference B — so every
coordinate reads off a real on-disk file and CodeDiffer fetches replacement text from the variant trees (no
text in the JSON). The conflict `kind` (modify/modify, modify/delete, add/add) is derivable from (ops,
baseLines), not stored. Golden vector `68cd14ac9a54521fc967f8c4632536bb9f0725cd394b8296cd1d62d4a9310e6a`
frozen in `digest-selftest` + `ConflictDigestTests`, from this input (records given out of order, to prove the
ordinal sort):

| section | records |
|---|---|
| conflicts-3way | `f.c · 5 · 1 · replace · 5 · 1 · replace · 5 · 1` and `a.c · 9 · 2 · replace · 9 · 2 · delete · 9 · 0` |
| merged-clean | `f.c · v1 · replace · 3 · 1 · 3 · 1` and `a.c · v2 · insert · 7 · 0 · 7 · 2` | The dial default produces replace/replace conflicts;
modify/delete, add/add, adjacent-edit, and identical-overlap-clean ride the `--conflict-edges` fixture (§3-way-edges).

### §3-way-edges (step 4b — the edge conflict kinds)

**Status: IMPLEMENTED.** `--three-way --conflict-edges` replaces the random odd-only single-line model with a
deterministic layout that exercises every conflict kind as a region **cleanly separated by ≥1 base line
untouched by both sides**. No digest-format change — same `Conflict`/`CleanMerge` records and
`conflictTruthSha` as step 4.

Truth is computed by the **union-span coalescer** (= diff3 maximal-change hunk), locked with CodeDiffer
2026-10-02:

- A conflict/clean **region** is a maximal run of consecutive base lines touched by *either* side; a base line
  untouched by *both* terminates the region. (Isolated edits ⇒ `baseLines=1`, degenerating to the step-4 model.)
- For overlapping/adjacent edits the region is the **union span**: v1 edits base 3–5, v2 edits 4–6 ⇒ ONE
  `Conflict{baseStart=3, baseLines=4}`. Each side's `newLines` = how many lines the region occupies in *its*
  tree (replace: `=baseLines`; delete side: `<baseLines`; insert side: `>baseLines`); `newStart` = that region's
  start in that tree (so a delete/insert elsewhere correctly shifts it).
- **kind** is derived by CodeDiffer from (ops, baseLines), never stored: modify/modify `(replace,replace)`;
  modify/delete `(replace,delete)` with `newLines=0` on the delete side; add/add `baseLines=0, (insert,insert)`.
- **identical-overlap** — both sides make the byte-identical edit to a region ⇒ it is **clean, not a conflict**:
  omitted from `conflicts`, emitted **once** in `merged-clean` with the canonical **`side="v1"`** (hard-coded on
  both tools, since `side` is in the digest bytes). CodeDiffer still recovers "agreed edit" from the trees
  (`B_v1 ≡ B_v2 ≠ B` over the region) — no manifest flag needed.
- The delete-side `newStart` = the 1-based line in that variant where the deleted region would begin (surviving
  lines before the cut + 1) — byte-identical to the 2-way delete convention. The add/add anchor `baseStart` =
  the base line *after which* both insert (= the unified `@@ -L,0 +M,k @@` anchor).

Fixture: `diff_fixture_3way_edge/` — one source file carrying all kinds (modify/modify, adjacent, modify/delete,
add/add, identical-overlap, one-sided-clean each side), with its own `conflictTruthSha` and a README mapping
each region to its kind.

**All four steps (+ the edge kinds) are now implemented and locked end-to-end.**

## 1.1.1 additions (post-v1.1.0, additive — manifestVersion stays 1)

### Single-locality giant (`--giant-edit single`)
See §"Giant files" above — one localized one-line insert into a giant, emitted as ONE explicit insert hunk
(not the strided run-rule), for the content-defined-chunker locality case.

### `--edit-kind mixed`
One delta whose files span every reason class: a deterministic per-file kind cycle
(`content,eol,whitespace,encoding,binary,metadata` by selection index). ≥6 content-bearing files ⇒ all six
branches appear in a single delta — with the standard honesty caveat that a kind which produces no byte change
on a degenerate file (e.g. `eol` on a terminator-less file) is dropped by the no-change guard, so that reason
would be absent. Each file's record carries its own `reason`; no schema or digest change. Renames remain a
separate pass (`--edit-kind rename`).

### `_meta.gen` — effective generation knobs (reproducibility)
The **gen** manifest (`<corpus>-manifest.json`) now carries a `_meta.gen` object: the EFFECTIVE knobs that
produced the corpus (post-scale counts + literal sizes/flags + seed/scale), i.e. the self-contained recipe to
regenerate the tree byte-identically. Additive and ignored by readers that don't look for it; it does **not**
enter any `truthSha` (it only changes the manifest file's own `baseManifestSha`, which is not a digest input).
A corpus genned pre-1.1.1 simply lacks the block. To replay it, pass every key back to `gen` as a knob
(camelCase → `--kebab-case`, `maxHeaderMB` → `--max-header-mb`, e.g. `--giant-headers=0 --no-newline=false`);
all counts are then explicit, so scaling is bypassed. The tree regenerates byte-identically, except
`compile_commands.json`, which embeds the absolute `--out` path. Before 1.1.5, `dirs` was recorded unscaled,
so a replay of a corpus that used the default (scaled) `--dirs` placed files in different directories.
`macroDensity` is present only when `--macro-density` was set: it caps giants at that many defines, while the
default leaves them uncapped (filled to `maxHeaderMB`). Before 1.1.6 it was always recorded, so a replay capped
every giant at 1M defines (a death-scale giant came back ~56 MB instead of ~1.2 GB) — in a pre-1.1.6
manifest a recorded `1000000` is ambiguous, so drop it when replaying unless the original run set it (e.g.
`--preset memory`).

### Paged / sharded delta (`--shard-size N`)
A death-scale 2-way delta can exceed what a consumer streams through `JsonDocument.Parse` whole. `--shard-size
N` (0 = monolithic default) switches the **2-way** delta to a paged transport:

- **`<corpus>-delta.index.json`** — the `_meta` (incl a `shardSize: N` marker + the sharding-invariant
  `diffTruthSha`), the inline `fileOps.added`/`removed`/`renamed`, and a `shards[]` catalog. It carries **no**
  `modified` array.
- **`<corpus>-delta.shard-NNN.json`** (000, 001, …) — each holds a `modified[]` array of ≤ N records, in
  path-ordinal order, **non-overlapping** across files, plus `shardIndex` + `count`.
- **`shards[]`** entries: `{ file, firstPath, lastPath, count, shardSha }` where `shardSha` = sha256 of the
  shard file's bytes (transport integrity). Paging is detected by the presence of the index / `shards[]`.

```json
"shards": [
  { "file": "death-delta.shard-000.json", "firstPath": "a/…", "lastPath": "f/…", "count": 1000, "shardSha": "…" },
  { "file": "death-delta.shard-001.json", "firstPath": "g/…", "lastPath": "z/…", "count":  742, "shardSha": "…" }
]
```

**`diffTruthSha` is SHARDING-INVARIANT.** It is computed over the full dedup-ordinal-sorted record set *before*
sharding, so a consumer that concatenates every shard's `modified[]` with the index's `fileOps` reproduces the
identical hash — the monolithic and paged forms are the same answer key, pure transport. `manifestVersion`
stays 1; the frozen golden vectors are untouched. (3-way variant deltas stay monolithic — sharding targets the
one monolithic 2-way delta.) A mutate run cleans up any prior delta artifacts (monolithic ↔ index+shards) so a
corpus never carries a stale mix.

## Sequencing (locked)

1. **hunk schema** + shas/sizes + `diffTruthSha` + the `digest-selftest` golden vector. ← CodeDiffer wires
   its verify-adapter against this first and reproduces the vector.
2. **reason-classes** + `--edit-kind` (one labeled fixture per class).
3. **rename** + graded similarities + decoys.
4. **joint 3-way** + `--overlap-fraction` + `conflictTruthSha` (pin its field orders here).

Each step lands with a labeled fixture + golden vector before the next. CodeSpawner pings the `code-spawner`
channel when (1)'s bytes are on disk.

## CodeDiffer side (mirrored, for reference)

- verify-adapter: assert `manifestVersion`, compose `base ⊕ delta`, independently reproduce `diffTruthSha`
  from the four canonical sections; reproduce the golden vector.
- regression suite seeded from the prototype-review bugs (eol/encoding "modified-with-empty-diff", one-sided
  binary, missing-path, doubled-newline/patch-applicability) so the step-2 reason-classes drop onto existing
  failing tests.
