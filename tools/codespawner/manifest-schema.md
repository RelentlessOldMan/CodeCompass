# Ground-Truth Manifest — Schema v1

`manifestVersion: 1`. This is the **contract** that couples the generator to its consumers
(CodeCompass `-Verify`, CodeCarver's oracle, and CodeSpawner's own `verify`). The corpus bytes may
change between generator versions; **this schema is the stable coupling point.** Both consumers MUST
hard-assert `_meta.manifestVersion` before trusting a manifest.

The generator writes the manifest as a sibling of the corpus directory: for `--out <dir>` it emits
`<dir>-manifest.json`.

## Shape

```json
{
  "_meta": {
    "manifestVersion": 1,
    "generatorVersion": "0.1.0",
    "seed": 1337,
    "corpusRoot": "C:\\...\\_fw"
  },
  "symbols": {
    "func_42": {
      "def": "block3/sub0/.../src_42.c:10",
      "refs": ["block3/.../src_43.c:12"],
      "edges": ["func_41"]
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

## `symbols`

A map keyed by **symbol name**. The symbol name — not the path — is the **seed-stable identity**:
`func_i` is always defined in `src_i.c` regardless of which directory a given seed drops it in.

| Field | Meaning |
|---|---|
| `def` | Definition site, `"<repo-relative-path>:<line>"`. |
| `refs` | Reference sites that MUST resolve, each `"<repo-relative-path>:<line>"`. |
| `edges` | Call-graph edges as **symbol → [symbol names]** (path-independent). For the linear chain, `func_i` has edge `func_{i-1}`. |
| `unreachableRefs` | *(optional)* Reference sites that are resolvable **only if a missing macro is known** — see below. |

### Paths

- All paths in `def`/`refs`/`unreachableRefs` are **repo-relative to the corpus root**, using
  **forward slashes**. One manifest therefore validates a local copy *and* an SMB copy of the same seed
  without any basename gymnastics.
- The line number is 1-based and points at the line containing the symbol token
  (`func_i(` for a definition or a call).

### `edges` — the call graph

`edges` is expressed as **symbol → [symbols]**, never paths. A reachability consumer can compute the
reachable set purely from `edges` without resolving a single path (paths are only needed for the
build/precision-reporting step). The generator emits the linear chain `func_i → func_{i-1}`; `func_0`,
`hot_shared`, and `vendor_gated` have empty `edges`.

### `unreachableRefs` — the negative case (dual semantics)

`unreachableRefs` lists sites that reference a symbol **behind `#ifdef VENDOR_OK`**, where `VENDOR_OK`
is defined only by a vendor header that is **absent from the tree**. This has two correct readings:

- **Indexer (closed-world):** the reference must **NOT resolve** — the macro is undefined, so the block
  is preprocessed out. Resolving it is a false positive (failing honesty, not passing recall).
- **Soundness-first carver (open-world):** MAY **keep** the `#ifdef VENDOR_OK` branch as a sound
  over-approximation (a macro it can't see might enable it). So for a carver this is a **closed-world
  precision** case (`--assume-defines-complete`), not a soundness one.

Schema note: `unreachableRefs` = "resolvable only if the missing macro is known."

## Stable invariants consumers may rely on

- `func_i` is defined in `src_i.c` and calls `func_{i-1}` (the linear chain). Keyed on the `src_<i>.c`
  basename / the `func_i` symbol — both seed-stable.
- `def`/`refs` remain `"<path>:<line>"`.
- The bimodal giant-header structure stays (it is the memory/scale test, not incidental).
- Adding a knob for one consumer never forces another to change (tool-specific knobs stay optional).
