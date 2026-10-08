# Ground-Truth Manifest — Schema v1

`manifestVersion: 1`. This is the **contract** that couples the generator to its consumers
(CodeCompass `-Verify`, CodeCarver's oracle, and CodeSpawner's own `verify`). The corpus bytes may
change between generator versions; **this schema is the stable coupling point.** Both consumers MUST
hard-assert `_meta.manifestVersion` before trusting a manifest.

> **Versioning rule** (agreed across consumers): *additive optional fields never bump `manifestVersion`;
> renames, removals, or semantic changes do.* New optional fields (`_meta.populations`, `expectedMiss`,
> `dupGroups`, and the **oracle-v1** block — `_meta.roots` / `_meta.indirectTruthSha` /
> `_meta.totalOracleBytes`, per-symbol `indirectEdges` / `bytes`, and non-linear DAG `edges`) all ship at
> v1 — old adapters ignore what they don't consume. The next real **v2** bump is reserved for when the
> schema grows for real (the C++ language profile: vtables, overrides, templates).

The generator writes the manifest as a sibling of the corpus directory: for `--out <dir>` it emits
`<dir>-manifest.json`.

## Shape

```json
{
  "_meta": {
    "manifestVersion": 1,
    "generatorVersion": "1.0.9",
    "seed": 1337,
    "corpusRoot": "C:\\...\\_fw",
    "roots": ["func_0"],
    "indirectTruthSha": "fa94...1572f",
    "totalOracleBytes": 19532
  },
  "symbols": {
    "func_42": {
      "def": "block3/sub0/.../src_42.c:10",
      "refs": ["block3/.../src_43.c:12"],
      "edges": ["func_41", "func_37"],
      "bytes": 128,
      "indirectEdges": [
        { "target": "itgt_3", "via": "fnptr", "dispatched": false, "resolved": true }
      ]
    },
    "vendor_gated": {
      "def": ".../vendor_gated.c:1",
      "refs": [],
      "edges": [],
      "unreachableRefs": [".../unres_0.c:5"]
    }
  }
}
```

## `_meta`

| Field | Meaning |
|---|---|
| `manifestVersion` | Schema version. Always `1` for this document. Consumers assert on it. |
| `generatorVersion` | CodeSpawner version that emitted the corpus. |
| `seed` | RNG seed. Consumers should assert this matches the corpus they think they have. |
| `corpusRoot` | The absolute `--out` dir at generation time. **Informational only** — do NOT resolve paths against it (the corpus may have been copied/staged on an SMB share). Resolve against the corpus dir you are actually reading. |
| `populations` | *(optional)* Per-population shape stats: `{ "<name>": {fileCount, totalBytes, identCount} }`. Lets a consumer assert the corpus is the shape it expects *before* trusting any pass/fail. Present for the header bands (`giant-headers`/`big-headers`/`med-headers`/`dense-band`) and the pathology populations (`broad-token`/`long-lines`/`encoding-mix`). Additive — absent on older manifests. |
| `roots` | *(optional, oracle-v1)* Array of **symbol-name strings** — the declared call-graph entry points. The reachable set is `BFS(roots)` over `edges`. Present when the oracle overlay runs (`--with-oracle`). See [`_meta.roots` + reachability](#_metaroots--reachability-oracle-v1). |
| `indirectTruthSha` | *(optional, oracle-v1)* Lowercase-hex SHA-256 **component digest** of the indirect-edge ground truth + `roots`, over a LOCKED canonical byte-form. Deliberately separate from the frozen primary `prevTruthSha`; a rewired indirect edge or moved root trips this. Present iff any symbol has `indirectEdges`. See [`indirectTruthSha`](#indirecttruthsha--the-canonical-component-digest-oracle-v1). |
| `totalOracleBytes` | *(optional, oracle-v1)* Sum of every symbol's `bytes` — the exact denominator for a byte-based reduction ratio. Present with `--oracle-bytes`. |

### Notable symbols

- **`hot_shared`** — a hot symbol called from every giant-including `.c`; its `refs` are the aggregate-query expected-set.
- **`broad_hot`** — (`broad-token` preset) one def, a large expected ref-set: every hot-token call site across the 2–8 MB carrier band, seeded at deterministic offsets including near-EOF. This is a `find_references`-at-scale expected-set *and* a block-selective ("sidecar") read-amplification test.
- **`vendor_gated`** — the negative case (see `unreachableRefs`).

## `symbols`

A map keyed by **symbol name**. The symbol name — not the path — is the **seed-stable identity**:
`func_i` is always defined in `src_i.c` regardless of which directory a given seed drops it in.

| Field | Meaning |
|---|---|
| `def` | Definition site, `"<repo-relative-path>:<line>"`. |
| `refs` | Reference sites that MUST resolve, each `"<repo-relative-path>:<line>"`. |
| `edges` | Call-graph edges as **symbol → [symbol names]** (path-independent). Linear default: `func_i → func_{i-1}`. With `--oracle-fanout` it is a **DAG** (a node may have several out-edges); see [DAG edges](#edges--the-call-graph). |
| `bytes` | *(optional, oracle-v1)* Byte mass of this symbol's definition span — the per-symbol weight for a byte-based reduction ratio. Present with `--oracle-bytes`; sums to `_meta.totalOracleBytes`. |
| `indirectEdges` | *(optional, oracle-v1)* Address-taken / indirect-call ground truth, one record per edge. See [`indirectEdges`](#indirectedges--indirect-call-ground-truth-oracle-v1). |
| `unreachableRefs` | *(optional)* Reference sites that are resolvable **only if a missing macro is known** — see below. |
| `expectedMiss` | *(optional, bool)* `true` = a lexical / preprocessor-blind indexer is EXPECTED not to resolve this symbol. The name is macro-synthesized (token-paste `##`) and never appears literally in the source; `def` points at the generator-macro invocation site. Not finding it is **correct**, not a recall failure — the honest-miss dual of `unreachableRefs`. See below. |

### Paths

- All paths in `def`/`refs`/`unreachableRefs` are **repo-relative to the corpus root**, using
  **forward slashes**. One manifest therefore validates a local copy *and* an SMB copy of the same seed
  without any basename gymnastics.
- The line number is 1-based and points at the line containing the symbol token
  (`func_i(` for a definition or a call).

### `edges` — the call graph

`edges` is expressed as **symbol → [symbols]**, never paths. A reachability consumer can compute the
reachable set purely from `edges` without resolving a single path (paths are only needed for the
build/precision-reporting step).

- **Linear default** (no `--oracle-fanout`): the chain `func_i → func_{i-1}`; `func_0` has no `func_*`
  edge, and `hot_shared` and `vendor_gated` have empty `edges`. Byte-identical to earlier releases.
- **DAG** (`--oracle-fanout k` [+ `--oracle-depth`, `--oracle-shared-leaves`]): each node has up to `k`
  out-edges; shared leaves give multiple callers a common sink (diamonds). Every `edges` target is still a
  declared symbol. Reachability is `BFS(_meta.roots)` — see below.

Edges to `hot_shared` (the hot symbol) depend on the mode. In an oracle overlay (`--with-oracle`) every
`func_i`, `func_0` included, also carries an edge to it. In a plain `gen` only the giant-including files'
`func_i` do (the first N, N = the giant-includer count), and with no giant headers (`--giant-headers 0`) there
is no `hot_shared` symbol at all. Exclude `hot_shared` when testing for "real" branching.

### `unreachableRefs` — the negative case (dual semantics)

`unreachableRefs` lists sites that reference a symbol **behind `#ifdef VENDOR_OK`**, where `VENDOR_OK`
is defined only by a vendor header that is **absent from the tree**. This has two correct readings:

- **Indexer (closed-world):** the reference must **NOT resolve** — the macro is undefined, so the block
  is preprocessed out. Resolving it is a false positive (failing honesty, not passing recall).
- **Soundness-first carver (open-world):** MAY **keep** the `#ifdef VENDOR_OK` branch as a sound
  over-approximation (a macro it can't see might enable it). So for a carver this is a **closed-world
  precision** case (`--assume-defines-complete`), not a soundness one.

Schema note: `unreachableRefs` = "resolvable only if the missing macro is known."

### `expectedMiss` — the honest-miss set (negative for a symbol extractor)

A symbol with `"expectedMiss": true` is generated by a **token-paste macro** (`patho{i}_handler_##id` →
`patho{i}_handler_{k}`), so its name never appears literally anywhere — a lexical / PP-blind indexer cannot
resolve it, and that is the *correct* behavior. `def` points at the `CS_MK_HANDLER(k)` invocation line (the
logical origin). A consumer should treat "not resolved" as a pass for these; resolving one is a bonus
(a full-PP indexer), never a requirement. CodeSpawner's own `verify` asserts the name is genuinely absent at
the def line (and the generator macro present), so the corpus really does hide it.

### `dupGroups` — duplicate-content oracle (top-level, optional)

Top-level `dupGroups` (sibling of `symbols`) maps a group name to a byte-identical file set plus optional
near-identical controls:

```json
"dupGroups": {
  "dup0": {
    "sha256": "<hex>",
    "paths": ["blockA/.../dup0_c0.c", "blockB/.../dup0_c1.c", ...],
    "nearVariants": ["blockC/.../dup0_near.c"]
  }
}
```

- **`paths`** are BYTE-IDENTICAL (each hashes to `sha256`) — the content-hash-dedup / posting-collapse
  target: an indexer must collapse them to a single posting set.
- **`nearVariants`** differ by ≥1 byte (different hash) — they must NOT collapse with the group.

Additive → absent on older manifests; consumers that don't dedup can ignore it.

## Oracle-v1 fields (non-linear DAG + indirect edges + byte mass)

These ship at `manifestVersion: 1` (additive). They are emitted by the `--with-oracle` overlay and are
**locked with CodeCarver + CodeCompass** (3-way agreement, 2026-09-30). Reproduce the digest against the
shipped golden vector (below) before trusting a recompute — the same gate that locked `prevTruthSha`.

### `_meta.roots` + reachability (oracle-v1)

`roots` is an array of **symbol-name strings** — the declared entry points. The reachable set is exactly
`BFS(roots)` over the `edges` graph (symbol → symbols). The **reachable fraction** is
`|reachable| / |symbols-in-graph|` and is **implicit** — there is *no* `_meta.reachableFraction` field;
derive it. `--oracle-reachable-frac f` dilutes the fraction toward `f` by adding **dead subgraphs**
(unreachable nodes); the reachable core is identical across variants generated at the same seed, so a graded
set (e.g. 0.25 / 0.50 / 0.75) differs only in dead mass. The verifier hard-errors if a declared root is not a
defined symbol.

### `indirectEdges` — indirect-call ground truth (oracle-v1)

Per-symbol array; each record is an indirect (address-taken) edge from the owning symbol:

```json
"indirectEdges": [
  { "target": "itgt_3", "via": "fnptr",        "dispatched": false, "resolved": true  },
  { "target": "iext_5", "via": "vector-table", "dispatched": true,  "resolved": false }
]
```

| Field | Meaning |
|---|---|
| `target` | Callee symbol name. Resolved targets are named `itgt_<k>` (defined in `_oracle/indirect_targets.c`); external ones `iext_<k>`. |
| `via` | The C construct the address is taken through. **Exactly one of** `fnptr` \| `vector-table` \| `init_array` (hyphenation is significant — both oracles bucket on this string). `vector-table` is the C flavor; a C++ vtable is a **v2** concern, not this. |
| `dispatched` | `true` = the corpus actually calls through the pointer (`acc += slot(x);`). `false` = the address is taken but **never invoked**. |
| `resolved` | `true` = `target` is a declared symbol in this manifest (closed-world). `false` = external — `target` is deliberately NOT a symbol. |

Two properties a carver oracle exercises:
- **Soundness** — a `dispatched` indirect edge is a real call; dropping its (reachable) target breaks soundness. Keep all dispatched targets.
- **Indirection tax** — a **reachable source** with a `dispatched: false` edge is a *legitimate over-keep*: address-taken-but-never-called is indistinguishable from a real target without whole-program dispatch analysis, so a sound carver keeps it. As of **1.0.9** the generator guarantees ≥1 such edge from a reachable function when `--oracle-indirect ≥ 2` (earlier builds welded `dispatched:false` to dead sources, leaving the tax quadrant empty).

Verifier checks: `resolved ⇒ target is a symbol`; `unresolved ⇒ target is NOT a symbol`.

### `indirectTruthSha` — the canonical component digest (oracle-v1)

`_meta.indirectTruthSha` is `sha256` (lowercase hex) over a **locked canonical byte-form** of the indirect
edges + roots. Byte-exact layout (US = `0x1F`, RS = `0x1E`, GS = `0x1D`; **both sections always present**,
empty = zero records):

```
indirectSection := for each edge, dedup then ORDINAL-sort the record string:
                     source US target US via US (dispatched?"1":"0") US (resolved?"1":"0")
                   join with RS, trailing RS after each record
rootsSection     := for each root, dedup then ORDINAL-sort:
                     name
                   join with RS, trailing RS after each record
digest           := sha256( indirectSection + GS + rootsSection )   // lowercase hex
```

- `source` is the **owning symbol name** (not the def path). `via` uses the same hyphenated spelling as the field.
- Dedup is by the full record string; sort is **ordinal** (byte-wise, not culture-aware).
- The empty case still emits the `GS`: an all-empty digest is `sha256("\x1d")`.

**Golden self-test vector** (frozen; ship-gate): the exe's `digest-selftest` command recomputes this fixture
and asserts it equals

```
fa9432bd75cd97b7d0a509f885f964b86b8ef41d449cc8c23b1b79df1aa1572f
```

from this exact input — `roots = ["func_0"]`, and edges:

| source | target | via | dispatched | resolved |
|---|---|---|---|---|
| `func_0` | `itgt_0` | `fnptr` | 1 | 1 |
| `func_0` | `iext_1` | `vector-table` | 0 | 0 |
| `func_1` | `itgt_0` | `init_array` | 1 | 1 |

Run `codespawner digest-selftest` — it prints both the primary (`7de5e47…`) and this indirect vector and
exits non-zero on any drift. A read-side recompute that reproduces `fa9432bd…` from the fixture above is
proven byte-identical to the generator and can be trusted on real manifests.

### `bytes` / `_meta.totalOracleBytes` — byte-mass ground truth (oracle-v1)

With `--oracle-bytes`, each symbol carries `bytes` (its definition-span byte mass) and `_meta.totalOracleBytes`
is their exact sum. This is the denominator for a **byte-based** reduction ratio (how much *mass*, not how many
*symbols*, a carve keeps) — most meaningful with `--oracle-scale`, which sizes bodies to the profile's measured
`.c` sizes. Absent (all `bytes` 0, total 0) by default.

## Stable invariants consumers may rely on

- `func_i` is defined in `src_i.c` and calls `func_{i-1}` (the linear chain). Keyed on the `src_<i>.c`
  basename / the `func_i` symbol — both seed-stable.
- `def`/`refs` remain `"<path>:<line>"`.
- The bimodal giant-header structure stays (it is the memory/scale test, not incidental).
- Adding a knob for one consumer never forces another to change (tool-specific knobs stay optional).
- When `--with-oracle` runs, `_meta.roots` is present and every root is a defined symbol; the reachable set
  is exactly `BFS(roots)` over `edges` (no separate reachability field to trust).
- `indirectTruthSha` reproduces byte-for-byte from `indirectEdges` + `roots` via the locked canonical form;
  prove your recompute against the golden `fa9432bd…` (`digest-selftest`) before relying on it.
