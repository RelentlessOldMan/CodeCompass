# Manual test prompts

A hands-on checklist for trying CodeCompass on **your own repo**. You give each prompt to a Claude Code (or
Codex) session that has CodeCompass installed, and compare what comes back with the **Expect** notes. It
covers all seven tools, plus the parts that run without being asked: keeping the index fresh as you edit,
and the Grep redirect.

It isn't a benchmark. Each check confirms that CodeCompass is wired in, is answering from its index, and
gives the shapes of answer described here. A full pass takes about 15 minutes.

## Before you start

1. Install the plugin (see the [README](../README.md#install)) and start a session **in the root of the repo you
   want to test**.
2. Run `/mcp` and confirm the **codecompass** server is connected.
3. Pick a few real names from your repo and use them wherever a prompt has a `<placeholder>`:

| Placeholder | Pick something like |
|---|---|
| `<distinctive text>` | A string that appears in only a few places, such as an error message or a config key |
| `<TypeName>` | A class, struct or interface defined once |
| `<functionName>` | A function or method that is called from several places |
| `<partial>` | Part of a name, e.g. `Parse` to find `ParseHeader`, `TryParse`, ... |
| `<CSharpMethod>` | *(C# repos only)* A method whose body calls other methods in the repo |
| `<other folder>` | *(optional)* The absolute path of a second, smaller code folder, for the linked-roots tests |

Results look like `path/to/file.ext:line:col: text`. Paths inside the project are relative to the repo
root; paths from a linked folder are absolute. Each answer ends with a count in parentheses, and any
caveat follows as a `(Note: ...)`. **A note is not a failure.** Notes are how CodeCompass tells you what an
answer does and doesn't cover.

The prompts name the tool, so the session calls exactly the tool being tested. Section 10 checks that an
agent picks the tools on its own.

---

## 1. Setup

**Prompt:** `List the CodeCompass MCP tools you have, one line each.`

**Expect:** seven tools: `search_code`, `find_definition`, `find_references`, `find_callees`, `search_symbols`,
`reindex`, `manage_links`. If they're missing, the server isn't connected; recheck `/mcp`.

**Prompt:** `Use CodeCompass reindex.`

**Expect:** `Reindexed N files (X MB) in Y s; M symbols.` N should be roughly the number of source files in
the repo. Build output (`bin/`, `obj/`, `node_modules/`, ...) is skipped. If the repo is very large, you get
instead a message to build once from a terminal with `codecompass index "<repo>"`. That's expected, not an
error: building that large an index inside a tool call would time out. Run the command, then continue.

## 2. Text search: `search_code`

**Prompt:** `Use CodeCompass search_code to find "<distinctive text>".`

**Expect:** one line per match, in the form `file:line:col: matched line`, ending with `(N matches)`. Check
one or two hits against the files: line and column should land exactly on the text.

**Prompt:** `Use CodeCompass search_code for "<distinctive text>" in a different case, caseSensitive false.`

**Expect:** the same hits. With the default (case-sensitive), a wrong-case query returns `No matches` and
a tip to retry with `caseSensitive:false`.

**Prompt:** `Use CodeCompass search_code for "if".`

**Expect:** a refusal: `"if" is too short to search: the index matches 3+ characters ...`, with a suggestion to
add surrounding text such as `"if ("`. Queries under 3 characters would read every file, so they're refused.

**Prompt:** `Use CodeCompass search_code for "return" with maxResults 5.`

**Expect:** exactly 5 lines, then `(showing the first 5 matches; MORE EXIST - narrow the query ... or raise
maxResults (max 1000))`. A cut-off answer always says so; it never presents itself as complete.

**Prompt:** `Use CodeCompass search_code for "zzqx_not_in_this_repo".`

**Expect:** `No matches for "zzqx_not_in_this_repo".` plus a tip. If some files were too large to index, a
note says so, so a zero is never silently "it doesn't exist".

## 3. Go to definition: `find_definition`

**Prompt:** `Use CodeCompass find_definition for <TypeName>.`

**Expect:** `file:startLine-endLine:col: Kind <TypeName>`, where Kind is Class, Struct, Interface, Function, ...
When there's exactly one definition and it's 40 lines or fewer, its source is shown inline below, with
line numbers, so you don't need to open the file.

**Prompt:** `Use CodeCompass find_definition for <functionName>.`

**Expect:** the same shape. A name defined in several places (overloads, or the same name in different
classes or files) lists each one with an `(N definitions)` count instead of inlining source. At most 50
are listed.

**Prompt:** `Use CodeCompass find_definition for <typename in the wrong case>.`

**Expect:** `No definition found ...` with tips. The match is exact and case-sensitive. Use `search_symbols` for
partial or case-insensitive lookups.

## 4. Symbol search: `search_symbols`

**Prompt:** `Use CodeCompass search_symbols for "<partial>".`

**Expect:** every symbol whose name contains `<partial>`, in any case, as `file:line:col: Kind Name`, ending with
`(N symbols)`. Symbols come from C#, C, C++, Python, JavaScript, TypeScript/TSX, Go, Rust and TRACE32
PRACTICE (`.cmm`). Other languages are text-searchable only, so they have no symbols here.

## 5. Find references: `find_references`

**Prompt:** `Use CodeCompass find_references for <functionName>.`

**Expect:** one line per use, ending with `(X C# semantic + Y name-matched reference(s))`. How the hits are
found depends on the language:

- **C#:** semantic (Roslyn), so these hits are counted under `C# semantic`. Only real uses of that symbol
  appear: no mentions in comments or strings, and no unrelated symbols that happen to share the name.
  The first C# lookup in a session builds the semantic model, which can take from a few seconds to
  tens of seconds on a big solution. Later lookups are fast.
- **C/C++:** matched by name, and counted under `name-matched`. You get whole-word uses in code,
  **never** inside a comment or a string, and **never** the definition itself. Expect the note:
  `C/C++ references are matched by NAME in code (comments and strings excluded; C/C++ is not compiled), so uses
  of different symbols that share this name are listed together.`
- **Python, JS/TS, Go, Rust, ...:** matched by name: whole-word uses in code files, never the definition
  itself.

To check it, add `// <functionName>` in a comment somewhere in a C# or C/C++ file and run the prompt again.
That comment line must **not** appear in the results.

**Prompt (C++ repos):** `Use CodeCompass find_references for <ClassName>::<method>.`

**Expect:** the calls to `<method>` (written `obj.<method>(...)` in code), with a note saying that the
qualified name was searched by its member name.

**Prompt:** `Use CodeCompass find_references for "ab".`

**Expect:** a note that a name this short is under the text index's 3-character minimum. Only C# semantic
references were searched; other languages were not.

## 6. Callees: `find_callees` (C# only)

**Prompt:** `Use CodeCompass find_callees for <CSharpMethod>.`

**Expect:** the in-repo methods it calls, each shown at its **definition** (`file:line:col: ...`), ending with
`(N callees)`. Calls into the framework or NuGet packages are left out. Ask for `find_callees` on one of the
results to walk the call chain down one level.

**Prompt:** `Use CodeCompass find_callees for <a non-C# function, or a C# method that only calls framework code>.`

**Expect:** a clear explanation, never a bare empty answer. Either `"<name>" is defined here, but calls no in-repo
methods ...` or `No symbol named "<name>" is indexed ...`.

## 7. Linked roots and focus: `manage_links` (optional)

You need `<other folder>` for this section.

**Prompt:** `Use CodeCompass manage_links to list linked roots.`

**Expect:** `No linked roots for <repo>. ...` on a fresh setup.

**Prompt:** `Use CodeCompass manage_links to add <other folder>.`

**Expect:** `linked: <other folder> (indexed N files, X MB, M symbols in Y s) — active on the next query.`
Instead you may see `(index already present - reused)` if it was indexed before, or an over-the-limit
message telling you to build it once from a terminal if it's large.

**Prompt:** `Use CodeCompass search_code for "<text that only exists in other folder>".`

**Expect:** hits from the linked folder, shown with **absolute** paths. `find_definition` and `find_references`
cover it too.

**Prompt:** `Use CodeCompass manage_links to focus on <other folder's name>.`

**Expect:** `Focused on 1 of 2 root(s): ... 1 root(s) excluded from searches until you clear focus ...`. Searches now
return hits from that folder only. `manage_links list` shows `Focus: ACTIVE`.

**Prompt:** `Use CodeCompass manage_links to clear the focus.`

**Expect:** `Focus cleared - searches federate across all 2 root(s) again.`

**Prompt:** `Use CodeCompass manage_links to remove <other folder> with purge.`

**Expect:** `unlinked: <other folder> (index deleted) — active on the next query.` If another project still links
that folder, its index is kept, and the message names that project.

## 8. Freshness: edits are picked up automatically

**Prompt:** `Add a comment containing the word zzqx_fresh_marker to any source file, then use CodeCompass
search_code for "zzqx_fresh_marker".`

**Expect:** the new line is found without running `reindex`. The watcher updates the index within a few
seconds of a save. If the first search runs too soon, ask again.

**Prompt:** `Remove that comment, then search for "zzqx_fresh_marker" again.`

**Expect:** `No matches`.

## 9. The Grep redirect (Claude Code only)

In Claude Code, CodeCompass installs a hook. A plain-text `Grep` over the whole project is sent to
CodeCompass; any search CodeCompass can't answer the same way still runs as a normal `Grep`. Codex has no
such hook, so there the redirect is only a suggestion to the agent.

**Prompt:** `Use the Grep tool (not CodeCompass) to search the whole repo for "<distinctive text>".`

**Expect:** the Grep call is **denied** with a message pointing to `search_code` and the other tools, and the
session then answers with CodeCompass.

**Prompt:** `Use the Grep tool to search for the regex "<word1>|<word2>".`

**Expect:** Grep **runs**. CodeCompass's text search is literal-only, so a regex is left to Grep. The same
applies to a pattern under 3 characters (`search_code` refuses those), a search scoped to a subfolder or one
file, a `glob` or `type` filter, context lines or counts, and a path outside the project. If the codecompass
server isn't connected (check `/mcp`), every Grep runs, since there's no `search_code` to send it to.

Set `CODECOMPASS_ENFORCE=0` in the environment to turn the redirect off completely.

## 10. Natural use

These prompts don't name a tool. The session should choose CodeCompass on its own.

**Prompt:** `Where is <TypeName> defined, and what uses it?`

**Expect:** the session calls `find_definition`, then `find_references`, and answers with exact
`file:line` locations. It shouldn't read whole files or list directories to find them.

**Prompt:** `Find every place that logs or throws "<distinctive text>" and summarize them.`

**Expect:** `search_code` first, then reads of only the few lines around each hit.

**Prompt:** `Walk me through what <CSharpMethod> does, one call level deep.` *(C#)*

**Expect:** `find_callees`, then `find_definition` or a short read for each callee.

---

## Quick checklist

| # | Check | Pass when |
|---|---|---|
| 1 | Tools connected | 7 tools listed; `reindex` reports files and symbols |
| 2 | `search_code` | exact `file:line:col` hits; short queries refused; truncation says MORE EXIST |
| 3 | `find_definition` | `Kind Name` with a line range; small single definitions inlined |
| 4 | `search_symbols` | case-insensitive partial matches with kinds |
| 5 | `find_references` | C# semantic; others by name; comments, strings and the definition excluded |
| 6 | `find_callees` | C# callees at their definitions; never a bare empty answer |
| 7 | `manage_links` | linked hits shown with absolute paths; focus narrows and says what it excluded |
| 8 | Freshness | a new string is searchable within seconds, without `reindex` |
| 9 | Grep redirect | plain whole-repo Grep redirected; regex, subfolder and glob searches still run |
| 10 | Natural use | the agent uses the tools on its own and doesn't read whole files |

## If something looks wrong

- Run `codecompass doctor "<repo>"` in a terminal. It prints a read-only health report: whether the index
  exists, loads cleanly, was built by the current version, and is on a network path.
- If results look stale after a large external change (a branch switch, a sync), use `reindex`, or run
  `codecompass index "<repo>"` from a terminal.
- If a note says the index was built by an older indexer, rebuild it as the note suggests.
- To report a problem, run `codecompass report "<repo>"`. It bundles the diagnostics and logs, never your
  source code, and prints exactly what it added so you can review it before sending.
