# `mutate` / churn — design doc (v1, for review)

**Status:** implemented (symbol mode since 1.0; bulk/diff mode since 1.1.0). Agreed shape from the
`claudes-chatroom` design session (2026-09-28). The bulk mode's delta contract lives in
[diff-delta-design.md](diff-delta-design.md).

## Why

Everything CodeSpawner emits today tests a **static** index. CodeCompass's incremental `update`, file
watcher, reconcile pass, and sidecar-delete / orphan-cleanup have **no ground-truth test** — they're
eyeballed. `mutate` deterministically edits a corpus in place and emits a **delta manifest** that composes
with the base (`post-edit truth = base ⊕ delta`), turning "looks right after an edit" into a pass/fail
assertion. CodeCompass is the consumer; CodeCarver (stateless batch carver) does not need it.

## Command surface

```
codespawner mutate --corpus <dir> --seed <S> --edits <N> [--step <k>] [--through]
```

- **In place.** Operates on an existing corpus; **guarded by the `.codespawner` marker** — refuses any dir
  CodeSpawner didn't create (never `--force`-clobbers here; mutate only edits its own corpus).
- **Journaled.** A `.codespawner-mutating` journal is written before the first edit and deleted after the last
  delta. A run that dies in between leaves it, and mutate then refuses the corpus and verify FAILs it: the
  tree matches neither the base nor any delta, so the only way on is to regenerate. A `--step` (or a step of
  the default run) that fails cleanly (file untouched, no delta) removes the journal, so the step can be
  retried. Every file edit is written to a temp file and renamed over the original.
- Reads the base `<corpus>-manifest.json` to know ground truth, then selects **N distinct target files**
  deterministically from `(S, k)` (distinct so edit order never conflicts).
- **`--step k`** — apply exactly edit `k` to the current on-disk state; emit `<corpus>-delta-{k}.json`
  (state_{k-1} → state_k, **prior-state relative**). This is the CHAIN driver.
- **`--through`** — apply all N edits; emit one cumulative `<corpus>-delta.json` (base → final). The
  batch / reconcile-in-one-pass case.
- **Determinism:** same `(corpus, N, S)` → byte-identical mutation + delta. Edit `k` is a pure function of
  `(S, k, targetFile[k])`; targets are chosen up front from S.
- **Fresh mtime is a hard contract:** every edited / added / renamed file gets a natural OS mtime bump
  (no preserve/restore). CodeCompass's quick-update + watcher paths are mtime-gated, so this is the whole
  point — a byte change under a stale mtime would (correctly) be missed by the incremental path. Remove
  drops the mtime with the file.

## Bulk mode (diff-oracle) — for the large-repo diff tool

A second, additive mode (2026-09-30) for a different consumer: a **2-/3-way diff tool** that needs the *same*
repo with a **controllable** set of changes, at scale. Where the edit taxonomy below is tuned for CodeCompass's
incremental/watcher paths (a handful of structural edits), bulk mode is tuned for "generate a variant where I
dial exactly **which** population changed, **how many** files, and **how much** of each."

```
codespawner mutate --corpus <dir> --target <source|headers|giant|all>
                   [--files-changed N] [--edit-density f] [--giant-min-mb N]
                   [--edit-kind <content|line-insert|line-delete|whitespace|eol|encoding|binary|metadata|mixed|rename>]
                   [--decoy-fraction f]   # rename only: near-duplicate ADD false-positive traps
                   [--seed S]
```

- **`--target`** selects a population by file property (corpus-agnostic — works on a `gen` corpus or a
  `--from-profile` one like `death_1.0.9`): `source` (`.c/.cc/.cpp/.cxx`), `headers` (`.h/.hpp/...`),
  `giant` (any file ≥ `--giant-min-mb`, default 100 — the 1 GB-header case), `all`.
- **`--files-changed N`** — how many matching files to change (default: all). The "10 vs 1,000 files" dial;
  files are chosen deterministically from `S`.
- **`--edit-density f`** (0..1, default 0.05) — fraction of each file's lines changed in place. The "a few vs
  a lot of changes within the file" dial — what makes a giant header interesting.
- **In-place edit:** ~`f` of each chosen file's lines get a deterministic marker comment appended (a file at
  or above `--giant-min-mb` is **streamed**, so a 1 GB header never loads into the heap) — a real textual
  change a diff tool sees,
  while **preserving the tokens already on the line** (def sites and the symbol table stay put, so the change
  is purely content).
- **Determinism:** same `(corpus, target, files-changed, density, S)` ⇒ byte-identical variant (same seeded
  `Rng`, confirmed cross-machine) — so a variant can be **regenerated in place** on another box, not copied.
- **Output:** one base→variant `<corpus>-delta.json` (`deltaKind:"diff"`): per changed file its `reason`,
  old/new sha and size, and unified hunks (a run-rule for giant files), plus `_meta.diffTruthSha`. Files whose
  bytes didn't change are not listed. `--shard-size` pages it; `--three-way` emits two variant deltas and a
  conflict file instead. Schema: [diff-delta-design.md](diff-delta-design.md).

### Ground-truth granularity (contract now LOCKED → see diff-delta-design.md)

Bulk mode first recorded only **file-level** truth (the changed-file set). **Hunk/line-level** truth,
**rename/move**, and **native 3-way** (two divergent variants + a conflict manifest) waited for the diff-tool
contract, which was **locked** with the CodeDiffer session (2026-10-02), frozen in
**[diff-delta-design.md](diff-delta-design.md)**, and is fully implemented as of 1.1.0: line/hunk truth in
unified-diff coords + a run-rule for giant files, a `reason` taxonomy (`content|eol|whitespace|encoding|
binary|metadata`) via `--edit-kind`, rename with a lines-preserved `similarityMilli` + decoys, native 3-way
via a joint generator with `--overlap-fraction`, and a `_meta.diffTruthSha` reusing the locked
`indirectTruthSha` canonical form. The engine keeps a clean seam: the per-line decision already knows the
changed line numbers, so emitting the hunk list is a localized add — no rework of the mutation mechanics.

## Edit taxonomy

Ranked by how much each stresses a real incremental indexer (top = where incrementals silently rot).

| # | Edit | v1? | What it catches |
|---|---|---|---|
| 1 | **remove** a file others reference | ✅ v1 | Stale entries lingering: reconcile must drop the file's symbols AND every ref site located in it. Absence assertion. |
| 3 | **modify (line-shift)** — insert lines above a symbol | ✅ v1 | Stale line numbers: symbol still exists at a new `path:line`; ripples to every symbol with a site in the file. |
| 5 | **add** a new file | ✅ v1 | Basic incremental pickup. |
| 4-grow | grow a small file **past the 8 MB sidecar cutoff** | ✅ v1 | Sidecar **CREATE** path. |
| 4-shrink | shrink a ~9 MB seed **below the sidecar cutoff (to <2 MB)** | ✅ v1 | Sidecar **DELETE** / orphan-cleanup — CodeCompass's riskiest untested path. Needs the straddle seed (below). |
| 4-restream | shrink a ~129 MB seed to ~100 MB | ✅ v1 (opt-in) | Sidecar **REWRITE** (streamed → whole-file). Distinct from DELETE. |
| 2 | **rename / move** | not built | FS-level remove+add; a naive incremental keeps BOTH paths. Symbol mode never renames (`fileOps.renamed` is reserved and omitted); the diff oracle's `--edit-kind rename` covers renames for CodeDiffer. |

> **Which threshold gates a sidecar (locked with CodeCompass):** a file has a positional sidecar iff
> `size >= the SIDECAR cutoff` (**8 MB local / 2 MB network-adaptive**). The **128 MB STREAM threshold**
> only decides streamed-vs-whole-file indexing, and *both* paths write a sidecar. So a 129 MB→<128 MB
> shrink only **rewrites** the sidecar (streamed→whole-file); it never orphans one. Only dropping under the
> **sidecar** cutoff deletes it.

### threshold-straddle seeds (enable 4-shrink / 4-restream in v1)

- **4-shrink (sidecar DELETE):** a gen option emits one deterministic **~9 MB** file (just over the 8 MB
  local cutoff, so it's sidecar'd in BOTH local and UNC/2 MB base indexes). mutate's 4-shrink truncates it
  to **<2 MB** (below both the 8 MB local and 2 MB network cutoffs) → the file no longer qualifies for a
  sidecar → CodeCompass's orphan-cleanup must **delete** the now-orphaned sidecar; `fileOps` asserts it's
  gone. Works identically local and over SMB.
- **4-restream (sidecar REWRITE, opt-in):** a *separate* deterministic **~129 MB** seed shrunk to **~100 MB**
  — exercises the streamed→whole-file sidecar rewrite. Gated behind a flag (the 129 MB seed bloats the base
  corpus), so the default mutate corpus stays lean.
- **4-grow** needs no seed — mutate appends filler to any small file to cross 8 MB (sidecar CREATE).

## Delta manifest schema

Sibling file, same convention as `<corpus>-manifest.json`:

```json
{
  "_meta": {
    "manifestVersion": 1,
    "deltaKind": "symbol",
    "baseSeed": 1337,
    "editSeed": 42,
    "step": 3,
    "baseManifestSha": "<identity sha of the base manifest this delta composes against>",
    "prevTruthSha": "<canonical digest of truth_{k-1}, the state this delta composes ONTO>"
  },
  "fileOps": {
    "added":    ["blockA/.../new_7.c"],
    "removed":  ["blockB/.../src_5.c"],
    "renamed":  [{ "from": "…/src_9.c", "to": "…/moved_9.c" }],   // reserved: omitted, symbol mode never renames
    "modified": ["blockC/.../src_3.c"]
  },
  "symbols": {
    "func_5":  "TOMBSTONE",
    "func_4":  { "def": "…/src_4.c:10", "refs": ["…"], "edges": ["func_3"], "removedSites": ["…/src_5.c:412"] },
    "func_3":  { "def": "…/src_3.c:20", "refs": ["…/src_4.c:12"], "edges": ["func_2"] }
  }
}
```

- **`_meta`** — `manifestVersion` stays **1** (the base schema is untouched; the delta is a new additive
  artifact). `deltaKind` is `"symbol"` (since 1.1.4; older symbol deltas omit it; diff deltas say `"diff"`,
  3-way conflict files `"conflict-3way"`). `baseManifestSha` binds a delta to the exact base it composes
  against, so it can't be misapplied. It is SHA-256 of the manifest bytes with every CR byte dropped and the
  `_meta.corpusRoot` string value replaced by `""` (since 1.1.4; before, the raw file bytes), so the same corpus
  regenerated at another path or on another OS binds the same deltas. Since 1.1.7 the corpus folder name is
  also dropped from `--linked-roots` paths: any string value starting `../<leaf>_root<digit>` (`<leaf>` = the
  last component of corpusRoot) is hashed as `../_root<digit>`, so the folder name `--out` used doesn't matter
  either. A manifest without linked roots hashes exactly as in 1.1.4–1.1.6. All JSON artifacts are written with LF
  line endings on every OS (since 1.1.4).
- **`fileOps`** — the FS-effect checklist. Composition never needs it; it drives CodeCompass's orphan-cleanup
  / old-path-gone asserts AND doubles as the changed-paths to replay as synthetic FS events into the watcher.
  mutate emits **no** changed-list to `update` itself — that reconcile is the thing under test.
- **`symbols`** — the churn set only: every symbol whose def/refs/edges changed, as its full new entry, or
  the string `"TOMBSTONE"` if removed. Optional **`removedSites`** per symbol lists ref sites that vanished
  (point-precise LEAK asserts: "this `path:line` must be gone from `find_references(X)`"), reusing the
  existing negative-oracle machinery.

### The ripple invariant (why symbol-keyed, not path-keyed)

> **Any symbol with a def OR ref site physically in the touched file changes** — even symbols *defined*
> elsewhere.

A ref site is recorded at the CALL site, in the caller's file. So editing `src_5.c` moves `func_5.def`
(defined there), `func_4`'s ref site (the call to func_4 lives in src_5), and `hot_shared`'s ref site if
src_5 is a giant-includer. All three are listed in `symbols`. Path-keying by def location cannot express
`func_4`/`hot_shared` changes; symbol-keying does, with no special cases.

### Composition rule (the adapter's `base ⊕ delta`)

```
truth.symbols = copy(base.symbols)
for (name, entry) in delta.symbols:
    if entry == "TOMBSTONE": truth.symbols.remove(name)
    else:                    truth.symbols[name] = entry     # add or replace
```

A trivial dict overlay — no ripple special-casing. Chain: `truth_k = truth_{k-1} ⊕ delta_k`.

## Chain integrity — `prevTruthSha` canonical digest

`baseManifestSha` binds the chain's STARTING base, but `delta_k` (k>1) composes onto `truth_{k-1}`, which
is never persisted. `_meta.prevTruthSha` is the canonical digest of the truth this delta expects to apply
ONTO; the adapter hashes its own in-memory composed truth before applying `delta_k` and catches any
out-of-order / misapplied / skipped delta mid-chain.

**Canonical digest (locked byte-for-byte with CodeCompass):**
```
digest = sha256( concat over symbols, ascending ORDINAL by name:
    name  0x1F  def  0x1F  refsSortedOrdinalJoinedByComma  0x1F  edgesSortedOrdinalJoinedByComma  0x1F  (expectedMiss ? "1" : "0")  0x1E )
```
Pins that keep the two implementations from drifting:
- **Ordinal (byte-wise UTF-8) sort** for BOTH the symbol-name ordering AND the refs/edges sort — never culture-aware.
- Hash input is the **UTF-8 bytes** of the serialization; separators are the literal bytes **0x1F** (unit) and **0x1E** (record), **including a trailing 0x1E** after the last record.
- **Paths verbatim** — the manifest's exact repo-relative forward-slash form; no normalization on either side.
- **`expectedMiss` absent ⇒ `"0"`** (so the near-universal symbols without the field hash identically).
- Field set (name, def, refs, edges, expectedMiss) is complete for v1; if a future edit adds a symbol
  field, extend the digest then — the delta `_meta` is versioned so the form can evolve unambiguously.

**Golden vector (unit-test your composer against this before running any chain):**
Truth = two symbols —
- `func_0` → def `block1/src_0.c:11`, refs `["block1/src_1.c:14"]`, edges `[]`, no `expectedMiss`
- `func_1` → def `block1/src_1.c:12`, refs `[]`, edges `["func_0"]`, no `expectedMiss`

Canonical serialization = **81 bytes**;
**`prevTruthSha = 7de5e47c16574fd481e461173401dbbe2c874e8712c61049b8830c3a78775d6e`** (sha256, lowercase hex).
CodeCompass independently reproduced this exact hash, so the two composers are confirmed aligned.

## Adapter flow (CodeCompass side)

**Chain (incremental) test:**
```
index base corpus
truth = base
for k in 1..N:
    codespawner mutate --corpus X --seed S --edits N --step k   # applies edit k, emits delta_k
    truth = truth ⊕ delta_k
    codecompass update X                                        # incremental; self-diffs (under test)
    assert live_index == truth                                  # + fileOps: orphaned sidecars gone, old paths absent, removedSites not in find_references
```

**Batch (reconcile-in-one-pass) test:** `mutate --through` → one `update` → assert against base ⊕ delta.

**Watcher test:** replay `fileOps` as synthetic FS events into the targeted-update path; assert same
composed truth.

## Determinism guarantees

- Target files chosen up front from `editSeed` (N distinct); edit `k` is pure in `(S, k, target[k])`.
- Same `(corpus, N, S)` ⇒ identical edits, identical delta bytes (path-normalized), identical mtimes-ordering
  is irrelevant (each file gets its own fresh mtime; determinism is over CONTENT + the manifest, not wall-clock
  timestamps — timestamps are never written into the delta).
- New RNG `Category.Mutate` (appended; existing streams unchanged).

## Out of v1 scope

- Recomputing `_meta.populations` / `dupGroups` after edits (edits target symbol-bearing source files; the
  delta focuses on `symbols` + `fileOps`). The doc will state populations may drift post-mutate and are not
  asserted in v1.
- rename/move (2) in symbol mode: not built (see the taxonomy table).

## Open implementation notes (not blocking sign-off)

- **Line-shift edit** inserts a deterministic block of filler lines above the target symbol's def; recompute
  every in-file site's new line from the insertion point.
- **remove** must scan the base manifest for any symbol with a site in the removed file and emit its updated
  entry (or tombstone if defined there) — this is the ripple set.
- **baseManifestSha** is computed from the base manifest at mutate time (path- and EOL-independent, see
  `_meta` above). CodeCompass's adapter does not read it; its chain check is `prevTruthSha`, recomputed from its
  own composed truth.
- mutate updates `<corpus>-manifest.json` in place too? **No** — the base manifest stays the immutable base;
  deltas are the record of change. (A `--rebase` to fold deltas into a new base manifest could be a later
  convenience.)
