# mutate

`mutate` deterministically edits a corpus in place and emits a **delta manifest** that composes with the base
(`post-edit truth = base ⊕ delta`). It gives an incremental indexer's `update` path, file watcher, reconcile
pass and positional-index delete / orphan cleanup a ground-truth test: "looks right after an edit" becomes a
pass/fail assertion. A stateless batch tool (such as a carver) does not need it.

It has two modes: **symbol mode** (below), a handful of structural edits with symbol-level truth, and
**bulk mode**, a controllable set of textual changes for a diff tool, whose delta format lives in
[diff-delta-design.md](diff-delta-design.md).

## Command surface

```
codespawner mutate --corpus <dir> --seed <S> --edits <N> [--step <k>] [--through]
```

- **In place.** Operates on an existing corpus; **guarded by the `.codespawner` marker**: refuses any dir
  CodeSpawner didn't create (never `--force`-clobbers here; mutate only edits its own corpus).
- **Journaled.** A `.codespawner-mutating` journal is written before the first edit and deleted after the last
  delta. A run that dies in between leaves it, and mutate then refuses the corpus and verify FAILs it: the
  tree matches neither the base nor any delta, so the only way on is to regenerate. A `--step` (or a step of
  the default run) that fails cleanly (file untouched, no delta) removes the journal, so the step can be
  retried. Every file edit is written to a temp file and renamed over the original.
- **One mutation per tree.** What has been applied is recorded in `<corpus>-mutated.txt` BESIDE the corpus
  (the tree and its marker stay byte-for-byte what a base-vs-variant diff expects): `step <S> <N> <k>` (symbol
  mode, edits 1..k applied, by `--step` or the default run), `through <S> <N>`, `bulk` (a 2-way bulk run), or
  `variant` (a 3-way `_v1`/`_v2` tree). The step and through lines end in ` restream` when the run used
  `--restream`, since that changes the plan. Mutate refuses a recorded tree, except `--step k` on
  `step <S> <N> <k-1>` with the same seed, edit count and `--restream`; `--step k>1` with no record is refused
  too. This stops a repeated step or a second bulk run from editing files that no longer match the base and
  writing a delta that cannot apply to it. `--three-way` leaves B as the base, so it records nothing for B and
  can repeat; each variant tree is recorded `variant`, so it is not mutated again against B's manifest. The
  record is written while the journal still exists, so a failure there leaves the journal (corpus refused)
  rather than a stale record. A regen deletes it, and the variants' with them. A corpus with no record is
  treated as unmutated.
- Reads the base `<corpus>-manifest.json` to know ground truth, then selects **N distinct target files**
  deterministically from `(S, k)` (distinct so edit order never conflicts).
- **`--step k`**: apply exactly edit `k` to the current on-disk state; emit `<corpus>-delta-{k}.json`
  (state_{k-1} → state_k, **prior-state relative**). This is the CHAIN driver.
- **`--through`**: apply all N edits; emit one cumulative `<corpus>-delta.json` (base → final). The
  batch / reconcile-in-one-pass case.
- **Determinism:** same `(corpus, N, S)` → byte-identical mutation + delta. Edit `k` is a pure function of
  `(S, k, targetFile[k])`; targets are chosen up front from S.
- **Fresh mtime is a hard contract:** every edited / added / renamed file gets a natural OS mtime bump
  (no preserve/restore). An indexer's quick-update and watcher paths are typically mtime-gated, so this is the
  whole point: a byte change under a stale mtime would (correctly) be missed by the incremental path. Remove
  drops the mtime with the file.

## Bulk mode (diff-oracle)

A second mode for a **2-/3-way diff tool** that needs the *same* repo with a **controllable** set of changes,
at scale. Where the symbol-mode edit taxonomy below is tuned for an incremental indexer (a handful of structural
edits), bulk mode is tuned for "generate a variant where I dial exactly **which** population changed, **how
many** files, and **how much** of each."

```
codespawner mutate --corpus <dir> --target <source|headers|giant|all>
                   [--files-changed N] [--edit-density f] [--giant-min-mb N]
                   [--edit-kind <content|line-insert|line-delete|whitespace|eol|encoding|binary|metadata|mixed|rename>]
                   [--decoy-fraction f]   # rename only: near-duplicate ADD false-positive traps
                   [--seed S]
```

- **`--target`** selects a population by file property (corpus-agnostic: works on a `gen` corpus or a
  `--from-profile` one): `source` (`.c/.cc/.cpp/.cxx`), `headers` (`.h/.hpp/...`), `giant` (any file ≥
  `--giant-min-mb`, default 100: the 1 GB-header case), `all`.
- **`--files-changed N`**: how many matching files to change (default: all). The "10 vs 1,000 files" dial;
  files are chosen deterministically from `S`. In `--three-way`, each variant's delta lists up to N: a picked
  file can draw no edits on one side.
- **`--edit-density f`** (0..1, default 0.05): fraction of each file's lines changed in place. The "a few vs
  a lot of changes within the file" dial, which is what makes a giant header interesting.
- **In-place edit:** ~`f` of each chosen file's lines get a deterministic marker comment appended (a file at
  or above `--giant-min-mb` is **streamed**, so a 1 GB header never loads into the heap): a real textual
  change a diff tool sees, while **preserving the tokens already on the line** (def sites and the symbol table
  stay put, so the change is purely content).
- **Determinism:** same `(corpus, target, files-changed, density, S)` ⇒ byte-identical variant (same seeded
  `Rng`), the same output on any machine and OS.
- **Output:** one base→variant `<corpus>-delta.json` (`deltaKind:"diff"`): per changed file its `reason`,
  old/new sha and size, and unified hunks (a run-rule for giant files), plus `_meta.diffTruthSha`. Files whose
  bytes didn't change are not listed. `--shard-size` pages it; `--three-way` emits two variant deltas and a
  conflict file instead. Line/hunk truth, the `reason` taxonomy, renames with `similarityMilli` + decoys, native
  3-way with `--overlap-fraction`, and the digests are all specified in
  [diff-delta-design.md](diff-delta-design.md).

## Edit taxonomy (symbol mode)

Ranked by how much each stresses a real incremental indexer (top = where incrementals silently rot).

The grow/shrink/restream edits target an indexer with CodeCompass's thresholds: a file gets a positional index
when it is ≥ **8 MB** locally (≥ **2 MB** over a network share), and a file ≥ **128 MB** is streamed instead of
indexed whole. Other indexers have other cutoffs; these seeds straddle those three.

| # | Edit | Built? | What it catches |
|---|---|---|---|
| 1 | **remove** a file others reference | yes | Stale entries lingering: reconcile must drop the file's symbols AND every ref site located in it. Absence assertion. |
| 3 | **modify (line-shift)**: insert lines above a symbol | yes | Stale line numbers: symbol still exists at a new `path:line`; ripples to every symbol with a site in the file. Keeps the file's encoding, BOM, each line's terminator and a missing final newline. |
| 5 | **add** a new file | yes | Basic incremental pickup. |
| 4-grow | grow a small file **past the 8 MB positional-index cutoff** | yes | Positional index **CREATE** path. |
| 4-shrink | shrink a ~9 MB seed **below the cutoff (to <2 MB)** | yes | Positional index **DELETE** / orphan cleanup. Needs the straddle seed (below). |
| 4-restream | shrink a ~129 MB seed to ~100 MB | yes (opt-in) | Positional index **REWRITE** (streamed → whole-file). Distinct from DELETE. |
| 2 | **rename / move** | no | FS-level remove+add; a naive incremental keeps BOTH paths. Symbol mode never renames (`fileOps.renamed` is reserved and omitted); bulk mode's `--edit-kind rename` covers renames for a diff tool. |

> **Which threshold gates the positional index** (in that indexer): a file has one iff
> `size >= the index cutoff` (8 MB local / 2 MB network-adaptive). The 128 MB stream threshold only decides
> streamed-vs-whole-file indexing, and *both* paths write the index. So a 129 MB→<128 MB shrink only
> **rewrites** it (streamed→whole-file); it never orphans one. Only dropping under the index cutoff deletes it.

### Threshold-straddle seeds

- **4-shrink (index DELETE):** a gen option emits one deterministic **~9 MB** file (just over the 8 MB
  local cutoff, so it is indexed positionally under both the local and the 2 MB network cutoff). mutate's
  4-shrink truncates it to **<2 MB** (below both) → the file no longer qualifies → the indexer's orphan cleanup
  must **delete** the now-orphaned index; `fileOps` asserts it's gone. Works the same locally and over a
  network share.
- **4-restream (index REWRITE, opt-in):** a *separate* deterministic **~129 MB** seed shrunk to **~100 MB**,
  which exercises the streamed→whole-file rewrite. Gated behind a flag (the 129 MB seed bloats the base
  corpus), so the default mutate corpus stays lean.
- **4-grow** needs no seed: mutate appends filler to any small file to cross 8 MB (index CREATE).

How the edits are made: the **line-shift** edit inserts a deterministic block of filler lines above the target
symbol's def and recomputes every in-file site's new line from the insertion point. **remove** scans the base
manifest for every symbol with a site in the removed file and emits its updated entry (or a tombstone if it
was defined there): that is the ripple set.

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

- **`_meta`**: `manifestVersion` stays **1** (the base schema is untouched; the delta is a separate additive
  artifact). `deltaKind` is `"symbol"` (diff deltas say `"diff"`, 3-way conflict files `"conflict-3way"`).
  Manifests from before 1.1.4 omit `deltaKind` on symbol deltas. `baseManifestSha` binds a delta to the exact
  base it composes against, so it can't be misapplied. It is SHA-256 of the manifest bytes with every CR byte
  dropped and the `_meta.corpusRoot` string value replaced by `""`, so the same corpus regenerated at another
  path or on another OS binds the same deltas. The corpus folder name is also dropped from `--linked-roots`
  paths: any string value starting `../<leaf>_root<digit>` (`<leaf>` = the last component of corpusRoot) is
  hashed as `../_root<digit>`, so the folder name `--out` used doesn't matter either. All JSON artifacts are
  written with LF line endings on every OS.
- **`fileOps`**: the FS-effect checklist. Composition never needs it; it drives orphan-cleanup / old-path-gone
  asserts AND doubles as the changed-paths to replay as synthetic FS events into a watcher. mutate emits **no**
  changed-list to the indexer's `update` itself: that reconcile is the thing under test. It lists only real
  changes: `removed` holds the source and those of its `.o`/`.lst`/`.bak` siblings that existed, and a Grow of
  a header already at the target size is not in `modified`.
- **`symbols`**: the churn set only: every symbol whose def/refs/edges changed, as its full new entry, or
  the string `"TOMBSTONE"` if removed. Optional **`removedSites`** per symbol lists ref sites that vanished
  (point-precise LEAK asserts: "this `path:line` must be gone from `find_references(X)`"), reusing the
  existing negative-oracle machinery. An entry carries the symbol's `indirectEdges` as the base manifest does;
  like the base, they stay out of `prevTruthSha` (a symbol whose only change is its `indirectEdges` is still
  listed).

The base `<corpus>-manifest.json` is never rewritten: it stays the immutable base, and deltas are the record of
change.

### The ripple invariant (why symbol-keyed, not path-keyed)

> **Any symbol with a def OR ref site physically in the touched file changes**, even symbols *defined*
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

A trivial dict overlay, with no ripple special-casing. Chain: `truth_k = truth_{k-1} ⊕ delta_k`.

## Chain integrity: `prevTruthSha` canonical digest

`baseManifestSha` binds the chain's STARTING base, but `delta_k` (k>1) composes onto `truth_{k-1}`, which
is never persisted. `_meta.prevTruthSha` is the canonical digest of the truth this delta expects to apply
ONTO; the adapter hashes its own in-memory composed truth before applying `delta_k` and catches any
out-of-order / misapplied / skipped delta mid-chain. (An adapter can rely on `prevTruthSha` alone for the chain
check, recomputed from its own composed truth, without reading `baseManifestSha`.)

**Canonical digest (byte-for-byte):**
```
digest = sha256( concat over symbols, ascending ORDINAL by name:
    name  0x1F  def  0x1F  refsSortedOrdinalJoinedByComma  0x1F  edgesSortedOrdinalJoinedByComma  0x1F  (expectedMiss ? "1" : "0")  0x1E )
```
Pins that keep two implementations from drifting:
- **Ordinal (byte-wise UTF-8) sort** for BOTH the symbol-name ordering AND the refs/edges sort, never culture-aware.
- Hash input is the **UTF-8 bytes** of the serialization; separators are the literal bytes **0x1F** (unit) and **0x1E** (record), **including a trailing 0x1E** after the last record.
- **Paths verbatim**: the manifest's exact repo-relative forward-slash form; no normalization on either side.
- **`expectedMiss` absent ⇒ `"0"`** (so the near-universal symbols without the field hash identically).
- The field set (name, def, refs, edges, expectedMiss) is complete; the delta `_meta` is versioned, so the
  form can evolve unambiguously if a symbol field is ever added.

**Golden vector:**
Truth = two symbols:
- `func_0` → def `block1/src_0.c:11`, refs `["block1/src_1.c:14"]`, edges `[]`, no `expectedMiss`
- `func_1` → def `block1/src_1.c:12`, refs `[]`, edges `["func_0"]`, no `expectedMiss`

Canonical serialization = **81 bytes**;
**`prevTruthSha = 7de5e47c16574fd481e461173401dbbe2c874e8712c61049b8830c3a78775d6e`** (sha256, lowercase hex).
Reproduce this before trusting your implementation.

## Adapter flow (indexer side)

**Chain (incremental) test:**
```
index base corpus
truth = base
for k in 1..N:
    codespawner mutate --corpus X --seed S --edits N --step k   # applies edit k, emits delta_k
    truth = truth ⊕ delta_k
    <indexer> update X                                          # incremental; self-diffs (under test)
    assert live_index == truth                                  # + fileOps: orphaned indexes gone, old paths absent, removedSites not in find_references
```

**Batch (reconcile-in-one-pass) test:** `mutate --through` → one `update` → assert against base ⊕ delta.

**Watcher test:** replay `fileOps` as synthetic FS events into the targeted-update path; assert same
composed truth.

## Determinism guarantees

- Target files chosen up front from `editSeed` (N distinct); edit `k` is pure in `(S, k, target[k])`.
- Same `(corpus, N, S)` ⇒ identical edits, identical delta bytes (path-normalized). Each file gets its own
  fresh mtime; determinism is over CONTENT + the manifest, not wall-clock timestamps, which are never written
  into the delta.
- mutate draws from its own RNG stream (`Category.Mutate`), so the gen streams are unchanged.

## Out of scope

- Recomputing `_meta.populations` / `dupGroups` after edits (edits target symbol-bearing source files; the
  delta focuses on `symbols` + `fileOps`). Populations may drift after a mutate and are not asserted.
- rename/move (2) in symbol mode: not built (see the taxonomy table).
