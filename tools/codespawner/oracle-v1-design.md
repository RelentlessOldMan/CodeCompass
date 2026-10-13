# Ground-truth oracle v1 extension — carve-correctness manifest

**Status:** LOCKED (3-way design chat, 2026-09-30, `claudes-chatroom`: CodeSpawner ↔ CodeCarver ↔ CodeCompass).
Additive to manifest schema **v1** — no `manifestVersion` bump. Build in progress.

## Why

The `--with-oracle` overlay emitted a *linear* C call chain (`func_i → func_{i-1}`). That makes carve
correctness untestable: precision is trivially ~100%, "reduction" is just chain position, and it never
exercises over-keep, indirect-edge soundness, or a graded reduction target. Every real carve bug CodeCarver
hits comes from **indirect edges** (function pointers, vector/dispatch tables, `init_array`) that were not in
the ground truth at all. This extension makes carve correctness testable against a known corpus — for the
first time without a real build.

## What ships (all additive, stays v1)

Build order: **1+3 together → 2 → 4**. C++ vtable/override/template stays the **v2** trigger (see ROADMAP).

### 1. Non-linear C call graph
A seeded DAG replaces the chain when enabled. Knobs (all off by default):
- `--oracle-fanout <n>` — out-degree per node.
- `--oracle-depth <n>` — layers from the root.
- `--oracle-shared-leaves <n>` — count of shared sink nodes multiple callers edge into (diamonds), so a
  reachable subset has real precision hazards.

**Default OFF = today's linear spine** → existing `GroundTruthOracleTests` + `carver-groundtruth-oracle.ps1`
stay byte-identical. No schema change — `edges` is already a per-symbol list; a DAG is just more edges.

### 3. Reachable-fraction dial (built with #1 — one generator)
- `--oracle-reachable-frac <0..1>` — generate so ~25/50/75% of symbols are reachable from the declared
  root(s); the rest are dead subgraphs → graded reduction assertions.
- **NEW optional `_meta.roots`**: array of declared entry-point symbol names. **Emitted even for the linear
  default** (so the consumer never guesses chain-middle again). A root that is not a declared symbol is a
  **hard gen-time error**.
- **Reachability = closure over `(edges ∪ indirectEdges)` from `_meta.roots`** — NOT call-graph-only. This is
  load-bearing: the dial's soundness property is "sound reachable-set includes the indirect closure," so
  computing the fraction without indirect edges would contradict #2.
- **Dial scatter:** indirect targets are spread across the cross-product
  `{reachable-source, dead-source} × {dispatched, not-dispatched}`, so a 25/50/75% corpus carries genuine
  over-keep rather than regrading a clean chain.

### 2. Indirect-edge ground truth (C)
Per-symbol **`indirectEdges`**, co-located beside `edges`, same keying. Each entry:

```json
{ "target": "<symbol>", "via": "fnptr|vector-table|init_array", "dispatched": true, "resolved": true }
```

- `via` ∈ `{ fnptr, vector-table, init_array }` — **C only**. `vtable`/`override` are C++ → **v2**.
- `dispatched: bool` — replaces the redundant `addressTaken` (address-taken is true by construction). It
  encodes the property CodeCarver actually asserts:
  - **SOUNDNESS**: a sound carve keeps **every** `indirectEdges` target, dispatched or not (statically you
    cannot prove a table entry is never invoked — keeping it is correct, not over-keep-as-bug).
  - **PRECISION / indirection tax**: the reachable + `dispatched:false` set is *counted* over-keep — a
    number (`safe − minimal`), the set a future trace/tightness tier could legitimately drop. Carried as a
    real population, never a failure.
- `resolved: bool` — **closed-world**: an external target (e.g. into libc/vendor) is `resolved:false` and is
  a **terminal leaf** (no outgoing edges; the reachability closure stops there).
- The generator emits the **real C constructs** so the address-taken targets exist in source: a function
  pointer table, a vector/dispatch table, an `__attribute__((constructor))` / `init_array` entry — including
  genuine never-dispatched address-taken targets.

#### Indirect-edge digest — no silent drift
The frozen primary digest **`prevTruthSha = 7de5e47c…`** stays **untouched and OUT** (neither CodeSpawner nor
CodeCompass re-signs). Indirect drift is caught by a **separate component digest**:

- **`_meta.indirectTruthSha`** — present only when any `indirectEdges` is. A rewired indirect edge or a moved
  root trips it. Guarded by a **second `digest-selftest` golden vector** in the exe (same emit↔reproduce gate
  as the first). CodeCarver additionally verifies it on read (a third independent reader).

**Canonical form (byte-exact, verbatim from the lock):**
- Ordinal (byte-wise) sort throughout.
- `US` = `0x1F` between fields; `RS` = `0x1E` between records (a trailing `RS` terminates every record).
- Per-indirect-edge field order: **source · target · via · dispatched · resolved**.
- Bools serialized as `0` / `1`.
- Dedup-then-sort each population before hashing.
- `GS` = `0x1D` between the `indirectEdges` section and the `roots` section.
- Both sections always emitted (empty section = zero records, i.e. it contributes nothing but the `GS` is
  always present between them).
- `sha256` of the assembled byte string, lowercase hex.

**Golden vector (frozen; CodeSpawner emits, CodeCompass/CodeCarver reproduce; guarded by `digest-selftest`):**
```
roots  = [ func_0 ]
symbols:
  func_0.indirectEdges = [ {itgt_0, fnptr,        dispatched, resolved},
                           {iext_1, vector-table, !dispatched, !resolved} ]
  func_1.indirectEdges = [ {itgt_0, init_array,   dispatched, resolved} ]
=> indirectTruthSha = fa9432bd75cd97b7d0a509f885f964b86b8ef41d449cc8c23b1b79df1aa1572f
```
(Byte string: three edge records — each `source␟target␟via␟dispatched␟resolved␞` — ordinal-sorted, then `␝`,
then the one root record `func_0␞`.)

### 4. Byte-mass ground truth (optional, last) — ✅ BUILT (2026-09-30)
Per-symbol byte size + a `_meta.totalOracleBytes`, behind **`--oracle-bytes`** (default off, so the default
manifest stays byte-identical). Lets a carve's reduction target be asserted in **bytes** — the unit the
GB-reduction goal uses — not just symbol count, and turns the indirection tax into a *cost*. Most useful with
`--oracle-scale`, where body inflation makes per-symbol sizes non-uniform.

**Granularity choice (flag for CodeCarver to confirm):** `bytes` = the **definition span** of each symbol
(opener line → closing brace), and `totalOracleBytes` = the exact sum of those. This is self-consistent
(reachable-bytes / total is well-defined) but excludes inter-function bytes (includes, forward-decls). If
CodeCarver wants **whole-file** granularity or the full corpus byte total as the denominator instead, that's
a one-line change — raise it and I'll adjust.

## Oracle properties this unlocks (CodeCarver)
- **SOUNDNESS** — a sound carve keeps every direct + indirect reachable target (FAIL if any dropped).
- **PRECISION / TAX** — the reachable + `dispatched:false` set is counted over-keep (the indirection tax).
- **GRADED reduction** — assertions at 25/50/75% via the dial.

## Backward-compatibility
`ManifestVerifier` and `ManifestReader` ignore unknown fields, so `indirectEdges`, `_meta.roots`,
`_meta.indirectTruthSha`, and per-symbol byte sizes are additive. Existing `verify` keeps passing until the
verifier is extended to check them.

## Build plan / tracking (no ticket system — checklist is the tracker)

**Phase 1+3 — seeded DAG + roots + reachable dial — ✅ DONE (2026-09-30)**
- [x] `GenOptions`: `OracleFanout`, `OracleDepth`, `OracleSharedLeaves`, `OracleReachableFrac` (+ ArgParser knobs, default off).
- [x] `OracleGraph` builder: deterministic node/edge model; linear when fanout=0, layered F-ary DAG otherwise; shared leaves (diamonds); dead subgraphs to hit reachable-frac.
- [x] `OracleOverlay` emits files from the graph model (was hard-coded chain) — **linear default proven byte-identical** (all `_oracle/*.c` + symbols block; `_meta` gains `roots` only).
- [x] `ManifestModel` + `ManifestWriter` + `ManifestReader`: `_meta.roots`.
- [x] Reachability closure over `edges` (∪ `indirectEdges` in phase 2) from roots; hard error on undeclared root.
- [x] `ManifestVerifier`: verifies roots are declared symbols.
- [x] Tests: `OracleGraphTests` (linear chain, DAG fan-out/diamonds, reachable fraction ≈ target, determinism) + `OracleOverlayTests` (edge/root wiring); 177 pass. `verify` PASS on linear/DAG/frac corpora.

**Phase 2 — indirect edges — ✅ DONE (2026-09-30)**
- [x] `IndirectVia` enum + `IndirectEdge` model + `SymbolEntry.IndirectEdges`; writer/reader (`{target, via, dispatched, resolved}`).
- [x] Emitters: fnptr / vector-table / init_array constructs in the source body, incl. genuine never-dispatched targets; resolved targets defined in `indirect_targets.c`, external targets left undefined.
- [x] `_meta.indirectTruthSha` canonical digest (`IndirectDigest`) + **second `digest-selftest` golden vector** (`fa9432bd…`).
- [x] Dial scatter across `{reachable-source, dead-source} × {dispatched, not}` + `--oracle-indirect <n>` knob. (Closure over indirectEdges is the consumer's to compute; the generator keeps the func-node dial as the fraction — noted simplification.)
- [x] `ManifestVerifier`: resolved target ⇒ declared symbol / unresolved ⇒ not; `indirectTruthSha` recomputed and matched (verifier is the third independent reader).
- [x] Tests: `IndirectDigestTests` (golden vector, order-independence, each field changes the hash, via round-trip) + `OracleOverlayTests` (scatter, closed-world targets, manifest round-trip, no-indirect default clean); 189 pass. `verify` PASS on an indirect corpus incl. `indirectTruthSha` reproduce.

**Phase 4 — byte-mass (optional) — ✅ DONE (2026-09-30)**
- [x] Per-symbol `bytes` (def span) + `_meta.totalOracleBytes`, behind `--oracle-bytes` (default off → default manifest byte-identical). Writer/reader + tests (per-symbol mass, consistent total, absent-by-default). Granularity (def-span) flagged for CodeCarver to confirm.

**Close-out**
- [ ] Full AOT `bench.ps1` + new oracle checks; cut a release (also ships the v1.0.8 ArgParser crash fix).
