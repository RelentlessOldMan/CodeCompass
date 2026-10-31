# C++ profile and manifest v2

`gen --lang cpp` writes a self-contained C++ program with a known call graph and a `manifestVersion: 2`
manifest. The decisions D1–D10 define the contract; "As built" below describes the generated program, the
additions to the contract, and the few places the generator narrows a decision.

## Why

The hardest reachability cases for a carver (CodeCarver, for example) are C++: virtual dispatch, overrides,
and templates. Without a generated corpus they can be checked only by building real repos, never against a
known call graph. Oracle v1 gives C a known direct + indirect graph; this extends that to C++. It is the change the
schema doc reserves `manifestVersion: 2` for, because C++ needs a symbol identity and an edge meaning that
v1 readers would misread.

## Scope

In: classes with single and multiple inheritance, virtual methods and overrides, pure virtuals, final,
constructors/destructors (incl. implicit calls), static-init objects, function templates and class templates
with explicit sets of instantiations, namespaces, member-function pointers (D10). Out (later or never): overloading on argument types beyond
what templates need, ADL, operator overloading, lambdas / `std::function`, RTTI / `dynamic_cast`,
exceptions, the standard library (the corpus stays self-contained, no `#include <...>`).

## Decisions

### D1. C corpora stay v1, byte-identical

Only a C++ corpus (`gen --lang cpp`, default `c`) writes `manifestVersion: 2`. Every existing knob and
corpus is untouched. A v1-only reader hard-asserts the version, so it rejects a C++ manifest instead of
misreading it.

### D2. Symbol identity: qualified name, no signatures

Symbols are keyed by their qualified name: `ns_1::Cls_4::m_2`, `ns_1::free_7`, `ns_1::Tmpl_3<int>::m_0`.
The generator never emits two overloads with the same qualified name, so a signature is never needed to
tell symbols apart. Each symbol gains `kind`: `function` | `method` | `ctor` | `dtor` | `template` |
`instance`. A `template` symbol is the uninstantiated definition; each `instance` symbol is one concrete
instantiation, and `instanceOf` names its template.

Each symbol also carries `simpleName` (`m_2` for `ns_1::Cls_4::m_2`). A name-keyed indexer (one that keys
its table on the simple name and searches refs by whole word) maps key → `simpleName`. The generator reuses
simple names on purpose (the same `m_2` in several classes, as overrides do), so a name-keyed tool sees
real collisions. Those collisions are derivable by grouping on `simpleName`, so no separate list is needed.

`def` is the line of the **definition with a body**, never a bodiless declaration: for a method declared
in-class and defined out-of-class, the out-of-class `void Cls_4::m_2() {` line; for an in-class inline
definition, that line. This matches an extractor that records only definitions with a body.

### D3. Virtual dispatch: a call site records both sound and precise target sets

A virtual call is recorded on the calling symbol as a `virtualEdges` record:

```json
{ "static": "ns_1::Base_0::m_2", "cha": ["ns_1::Base_0::m_2", "ns_1::D_1::m_2", "ns_1::D_3::m_2"],
  "rta": ["ns_1::D_1::m_2"] }
```

- `static`: the method the call resolves to through the receiver's static type: the receiver class's own
  implementation, or the one it inherits (then `static`'s class is an ancestor of the receiver's).
- `cha`: every non-pure implementation that a class at or below the RECEIVER's class resolves the method to
  (class-hierarchy analysis). This is the **sound** set; a carver that drops one fails soundness.
- `rta`: the subset that a class constructed somewhere reachable resolves to (true rapid type analysis, not
  "constructed anywhere in the corpus"). This is the **precise** set. `cha − rta` is the C++ indirection tax: a counted over-keep, never a failure, just
  like `dispatched:false` in oracle v1.
- A pure-virtual method is never in either set. A `final` class or method bounds `cha`. `cha` can be empty (a
  call through an abstract class with nothing concrete below it).
- The receiver class is not recorded (a consumer that wants it reads the call line). Two calls with the same
  `static` through different receivers can have different sets; each keeps its own record.

### D4. Direct edges, and implicit ctor/dtor edges in their own list

`edges` keeps its v1 meaning: direct calls written in the source (non-virtual methods, free functions, calls
through a qualified name like `Base::m()`, and an explicit construction `Cls_4 x;` / `new Cls_4` to its ctor).
The calls the compiler adds go in a separate `implicitEdges` list on the calling symbol: a ctor to its base
and member ctors, a dtor to its member and base dtors, and leaving scope to the dtor. `implicitEdges` are
ALWAYS part of both reachability sets (dropping one breaks the link), and cannot be turned off; they are kept
apart only so a consumer can count the keeps that came from implicit calls alone, the edges a name-based tool
most often misses. An implicit call has no name token, so it adds no ref site.

Ref sites follow the source's name tokens (whole word, outside comments and strings). An explicit construction
`Cls_4 x(…)` / `new Cls_4` is a ref site of the ctor `ns_1::Cls_4::Cls_4` (simpleName `Cls_4`), and an
explicit `x.~Cls_4()` call is a ref site of the dtor. A static-storage object's ctor is reached from a new root kind (D6), the C++ analog of
`init_array`.

### D5. Templates

A template body is reachable only through its instances. `edges` from an `instance` point at other
instances, never at the template. Each template has a fixed, generator-chosen instance set, and the
manifest lists them all, so an "instantiated but unreachable" instance is a real dead-code population.
Unused templates (no instances) are a population a carver can drop entirely. Truth for a carver that grades
per source definition (a template is one definition to it): a template is reachable iff at least one of its
instances is.

### D6. Roots

`_meta.roots` stays an array of symbol names, plus a new optional `_meta.staticInitRoots` for the ctors of
static-storage objects. Reachability is `BFS(roots ∪ staticInitRoots)`
over `edges ∪ implicitEdges ∪ virtualEdges.cha ∪ memberPtrEdges` (sound) or over
`edges ∪ implicitEdges ∪ virtualEdges.rta ∪ memberPtrEdges[dispatched]` (precise). Oracle-v1
`indirectEdges` (C fn pointers) stay valid in a C++ corpus and keep their meaning.

### D7. Component digest

A new `_meta.cppTruthSha` over a fixed canonical form (US/RS/GS, ordinal sort, dedup, the same
construction as `indirectTruthSha`): virtual-edge records, then `implicitEdges`, then `memberPtrEdges`, then
`kind`/`instanceOf` records, then `staticInitRoots`. Its golden vector is under "As built". `prevTruthSha` and
`indirectTruthSha` keep their exact current definitions.

### D8. Knobs (all classic-gen; `--lang cpp` required)

`--cpp-classes <n>`, `--cpp-depth <n>` (inheritance depth), `--cpp-multi-inherit <f>`,
`--cpp-virtual-frac <f>`, `--cpp-instantiated-frac <f>` (drives `cha − rta`), `--cpp-templates <n>`,
`--cpp-instances <n>`, `--cpp-static-init <n>`. The existing oracle knobs (fanout, depth, reachable-frac)
apply to the free-function and method graph. Defaults are picked to be small and to populate every
quadrant at least once.

### D9. Verify

`verify` checks every new field: `cha` is exactly the override set of a receiver at or below `static`'s class;
`rta ⊆ cha`; each `instance` has an existing `instanceOf`; every def/ref line really contains the name;
`cppTruthSha` recomputes. (As built, below, lists every check, and what verify does not prove.)

### D10. Member-function pointers

The C++ form of oracle-v1 `indirectEdges`: `&Cls_4::m_2` stored in a table or passed as data, and called
through `->*` / `.*`. Recorded on the symbol that takes the address as `memberPtrEdges`:
`{ "target": "ns_1::Cls_4::m_2", "via": "table" | "arg", "dispatched": true|false }`. Taking the address keeps
the target in the sound set; `dispatched:false` (address taken, never called through) is a counted over-keep,
exactly like v1. A pointer to a virtual method dispatches over that method's `cha`/`rta` sets.
The `&Cls_4::m_2` token names the method, so that line is recorded in `m_2`'s `refs` (as an address-taken
function name is in v1). The `->*` / `.*` call has no name token: no ref site, an expected miss for a
name-based indexer that `dispatched` lets it classify.

## As built

`gen --lang cpp --out <dir>` writes ONLY a self-contained C++ program under `<dir>/cpp/` plus
`<dir>-manifest.json` (v2). The C population knobs (`--scale`, `--with-oracle`, `--preset`, …) are refused with
`--lang cpp`, as is any `--cpp-*` knob without it. It takes `--out/--seed/--force`, the `--cpp-*` knobs (D8),
and `--oracle-chain/-fanout/-depth/-shared-leaves/-reachable-frac`, which shape the free-function graph.

**The program.** The program is checked to compile and link (`clang++ -std=c++17 -Wall -Wextra -Werror`) across
seeds and knob extremes. It is NOT meant to run: virtual and member calls go through `gp_Cls_N` receiver
pointers that are null, and method↔free-function cycles exist. All truth is static.
- Layout: `cpp/ns_k/free.cpp` holds the free functions `ns_k::free_i`. `cpp/ns_k/cls_i.hpp` holds a class, and
  `cls_i.cpp` its out-of-class bodies (every third class is all-inline in its header). `cpp/ns_k/mix_j.hpp`
  holds a mixin. `cpp/tmpl/tmpl_t.hpp` holds a template. `cpp/rt/` holds the prototypes, receiver pointers,
  member-pointer helpers (`cs_rt::mp_run_*` / `mp_keep_*`), `static_init.cpp` and `main.cpp` (which calls the
  root). File names are lowercase, so an `#include` line never contains a symbol's whole-word name.
- Free functions: the oracle graph (linear chain, or a DAG with `--oracle-fanout`) is the live part. A dead
  block follows: by default (`--oracle-reachable-frac 0`) a quarter of the live count, at least 2;
  `--oracle-reachable-frac f` sizes it so live/total ≈ f, and 1 means none. Method/ctor/dtor/template bodies
  call only live free functions, so the dead block stays dead. The live graph plus dead block is capped at
  2,000,000 functions.
- Hierarchy: classes 0..`--cpp-depth` form one guaranteed chain (`--cpp-depth` + 1 classes, `--cpp-depth`
  inheritance edges); each later class takes a base 70% of the time, one shallow enough to keep within
  `--cpp-depth` (so `--cpp-depth 0` makes every class a root). With two or more
  classes, class 0 is abstract (a pure virtual) and, when `--cpp-multi-inherit` > 0, class 1 takes a mixin
  (`Mix_j`, one pure `hook_j`), so multiple inheritance exists. With `--cpp-classes 1` the lone class is
  concrete and there are no mixins. `final` classes are concrete leaves that add no new virtual. Every class
  has a user ctor and dtor; mixins have neither.
- Construction: each instantiated class is constructed in live code (`Cls obj;` for an even index, `new` for
  an odd one); 60% of the other concrete classes are constructed in dead code. The static objects are drawn
  from the instantiated classes, or from all concrete classes when none is (so `--cpp-instantiated-frac 0`
  still constructs the static objects' classes). One more construction is reachable only through dispatch:
  an instantiated class's own virtual implementation, called through that class from live code, constructs a
  class nothing else reachable constructs, so the RTA solve below needs a second pass that adds a class (at
  least three passes, counting the last, which adds nothing). (Skipped when the program has no such class, when
  every instantiated class has a static object, or a static object's member, at or below it — rare at the
  default knobs, the rule at `--cpp-static-init 20` — or when fewer than two concrete classes are never
  constructed in reachable code.)
- Members: 30% of classes hold one member object of an instantiated class (not an ancestor). One more member
  makes a class live only through a base subobject: an abstract class with a constructed subclass holds a
  member of a concrete class unrelated to it and never constructed in reachable code (dead code may construct
  it), which is then live only because the subclass's complete objects contain it. (Skipped when no such pair
  exists — in practice at `--cpp-depth 0`, and at `--cpp-depth 16` with the default 12 classes — or when it would leave
  fewer than two such classes for the dispatch-only construction.)
- Templates alternate: a function template `tf_t` (symbol `ns::tf_t`, instance `ns::tf_t<int>`) and a class
  template `Tmpl_t` with one method (symbol `ns::Tmpl_t::m_0`, instance `ns::Tmpl_t<int>::m_0`). An instance's
  `def` is its template's body line. With two or more templates, the last is never used. Template 0 gets
  `--cpp-instances` type arguments (`int`, `long`, …, the first from live code) and, when there is dead code
  (`--oracle-reachable-frac` < 1), one more instance (`<bool>`) called only from dead code: instantiated but
  unreachable. Every other used template gets 1..`--cpp-instances`. The class-template name `Tmpl_t` itself
  (its `struct` line, `Tmpl_t<int> tv_n;`) is not a symbol: out of scope.

**Additions to the contract** (all additive, v2 only):
1. `pure: true` on a pure virtual method (absent otherwise). It has no body, so its `def` is the
   `virtual void m_0() = 0;` declaration line: the one exception to the D2 def rule. (A body-only extractor
   finds no def for it; `pure` lets it classify that as a known gap.)
2. `_meta.classes`: `{ "ns::Cls_4": ["ns::Cls_1", "ns::Mix_0"], … }`, every class and mixin with its direct
   bases. `verify` uses it to check that each `cha` target sits at or below `static`'s class.
3. `staticInitRoots` lists BOTH the ctor and the dtor of each static-storage object (the dtor runs at exit;
   D6 named only the ctor). The `ns::Cls_4 g_static_0;` line is a ref site of the ctor.
4. `lang: "cpp"` in `_meta`, and the `--cpp-*` / oracle knobs in `_meta.gen`.
5. **Class symbols** (`kind: "class"`, every class and mixin).
   Key `ns_1::Cls_4`, simpleName `Cls_4`, `def` = the `class Cls_4 … {` line. Its `refs` are EVERY other line
   where the name appears as a whole word outside strings and `#include`s: forward declarations, base lists,
   receiver-pointer and member declarations, `Cls_4::` qualifiers on out-of-class bodies, ctor and dtor
   declarations and bodies (`~` is not a word character, so `Cls_4::~Cls_4() {` is one), constructions,
   super-calls, member-pointer types and `&Cls_4::m` lines. So the def + refs of everything with simpleName
   `Cls_4` (the class and its ctor) is exactly what a whole-word name search returns. A class symbol has no
   edges of any kind and is outside reachability: graders skip `kind: class` the way they skip `template`
   (both stay out of a carver's denominators). It is in `cppTruthSha` through the kinds section.

**Narrowed by the generator:**
- Member-function pointers target NON-virtual methods only. The D10 rule for a pointer to a virtual method
  (dispatch over its `cha`/`rta`) stays reserved and is not generated yet.
- `new Cls_4()` assigns a receiver pointer and is never deleted, so a `new` adds no dtor edge. A local
  `Cls_4 obj;` adds the implicit dtor edge at scope exit. The generator writes no explicit `~Cls_4()` call.
- Declarations are neither def nor ref (as in v1): an in-class declaration of an out-of-class body, and the
  prototypes in `cpp/rt/free.hpp` and `cpp/rt/helpers.hpp`. (A class symbol is the exception: its name in
  those lines is a type use, so they are among its refs.)

**Statement forms and what each records** (on the enclosing body's symbol):

| Source line | Records |
|---|---|
| `ns_1::free_3();` | `edges` + a ref of `ns_1::free_3` |
| `gp_Cls_4->m_7();` (non-virtual) | `edges` + ref |
| `gp_Cls_1->m_0();` (virtual) | a `virtualEdges` record + a ref of `static` |
| `ns_0::Cls_0::m_0();` (in an override) | `edges` to the named implementation + ref |
| `ns_0::Cls_4 obj_3;` | `edges` → ctor, `implicitEdges` → dtor, ref of the ctor |
| `gp_Cls_4 = new ns_0::Cls_4();` | `edges` → ctor, ref of the ctor |
| `ns_0::tf_2<int>();` / `tv_5.m_0();` | `edges` → the instance + ref (the class-template ref is the `.m_0()` line) |
| `&ns_0::Cls_1::m_3,` in a `static … tbl_N[]` | `memberPtrEdges {via: table}` + a ref of the target; `dispatched` iff a `for (auto f : tbl_N) (gp->*f)();` follows |
| `cs_rt::mp_run_Cls_1(&ns_0::Cls_1::m_3);` | `edges` + ref of the helper, `memberPtrEdges {via: arg}` + ref of `m_3`; `mp_run` dispatches, `mp_keep` only stores |

A ref line inside a template body counts once for each instance that reaches it, as a ref of that instance's
substituted target (`tf_0<T>()` in `Tmpl_1<int>::m_0` is a ref of `tf_0<int>`). A `template` symbol itself has
no edges. The body of a template that is never instantiated still names things: there, a free-function call is
a ref of the function, and `tf_0<T>()` / `tv_n.m_0()` (on a `Tmpl_1<T>`) are refs of the template symbols
`ns::tf_0` / `ns::Tmpl_1::m_0`, with no edges. Those are the only refs a template symbol has, so a whole-word
search finds no line that no symbol owns.

**RTA.** A class is live when a static object, or a body reachable on the precise graph, constructs it as a
complete object; the member objects of a live class, including those its base classes declare, are live too.
A base subobject does not make its own class live. `rta` = the `cha` targets that some live class in the
receiver's subtree dispatches to. Reachability over `rta` can construct more classes, so the generator iterates
to a fixpoint, and the dispatch-only construction above makes a second pass add a class.

**Verify (v2):** accepts versions 1 and 2; any other version exits 2. A manifest with a duplicate key anywhere
exits 2, as does a `_meta` that is not an object, a `seed` that is not a 32-bit integer, or a `generatorVersion`
/ `corpusRoot` that is not a string. A v2 manifest of the wrong shape (a field of the wrong JSON type, a
symbol with no string `def`, a v1-only field such as `bytes`, a root `dupGroups` or `_meta.indirectTruthSha`,
a virtual or member-pointer record missing a field) exits 2 before any check runs.

What verify proves, and what it does not: it proves the manifest is **internally consistent** (kinds, keys,
hierarchy, record shapes, `cha` against the hierarchy) and that its **sites are real** (each def is a
definition line in the right file, each ref a line naming the symbol that is neither a declaration nor
another definition, and every definition line in the program belongs to some symbol). It does **not** re-read
calls from the program: `edges`, `rta`, `implicitEdges`, `staticInitRoots` and memberPtr `dispatched` are
checked only for the right kinds of target, so a wrong-but-well-formed one passes. `cppTruthSha` covers those
fields, but verify recomputes it from the manifest itself, so it catches an edit that was not re-signed, not a
re-signed one. The truth of those fields is carried by the generator's unit tests (an independent recompute of
liveness and `rta` from the emitted program) and by clang compiling the program. Then it checks:
- `_meta.lang` is `cpp`; `_meta.classes` is acyclic, names only classes as bases, and holds exactly the
  `kind: class` symbols; each class's bases are the ones its `class` line names, in order;
- each symbol has a known `kind` that fits its key (a `<` makes an instance; a key under a class is a ctor,
  dtor or method by name; a template's def is under `cpp/tmpl/`), and `simpleName` is the key's last name;
- def/ref lines contain the simpleName as a whole word (a ctor's not right after a `~`); sites are canonical
  `dir/file:line`; defs are unique (an instance shares its template's); no symbol lists its def or a site
  twice in `refs`;
- where sites sit (class symbols, whose names are type uses on any line, excepted): a def is a definition line
  (a body opening `{`, or a pure `= 0;`); a member's def is in its class's `.hpp` / `.cpp`, a function's in no
  class or template file; no site is a declaration (the `cpp/rt/*.hpp` prototypes, an in-class `void m_1();`,
  `Cls_3();` or `~Cls_3();`); no ref is a definition line (an override's `void m_1() override {` names the
  method it overrides but does not call it; `Cls_3::~Cls_3() {` names the class but calls no ctor);
- completeness: every definition line under `cpp/` (a function, method, ctor or dtor body, a pure `= 0;`, a
  `class` line) is some symbol's def, so a symbol deleted from the manifest fails (`int main()` is not a
  symbol);
- `edges` never target a `class` or a `template` (a call reaches a function, member or instance);
- a `class` symbol has no edges; `instanceOf` is a `template`, only an instance has one, and an instance's def
  is its template's;
- `pure` is set exactly on the methods whose def line ends in `= 0;`;
- implicit targets and `staticInitRoots` are ctors or dtors, member-pointer targets are methods, and `via` is
  `table` or `arg`;
- per virtual record: `static` is a method; `cha` holds no pure method and no duplicate, and is exactly the
  override set of some receiver at or below `static`'s class that resolves the call to `static`; `rta ⊆ cha`;
- `cppTruthSha` is present and recomputes (an unsigned edit, not a re-signed one; exact `rta` needs the
  program's constructions, which verify does not read).

`mutate` refuses a v2 manifest (it edits C corpora only) with exit 2, before touching anything.

**Golden vector** (`digest-selftest`, fifth line): `cppTruthSha =
28b4e1de981d282a6918de91846e990955f5b8220c88efc993d2a984c0bfed08` over the symbol set in
`CppDigestTests.GoldenSyms` with `staticInitRoots = ["ns::A::A", "ns::A::~A"]`. Reproduce this before trusting
your implementation.
