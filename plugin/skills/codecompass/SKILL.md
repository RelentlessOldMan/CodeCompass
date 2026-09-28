---
name: codecompass
description: Prefer CodeCompass MCP tools for code search and navigation in large repos, instead of grepping or reading whole files.
---

When you need to search or navigate code in this workspace, prefer the **CodeCompass** MCP tools over
scanning files by hand. They return precise `file:line:col` ranges and cost far fewer tokens than
grepping or reading whole files, and the index auto-updates as files change.

- **search_code** — literal text / substring search across the indexed tree.
- **find_definition** — jump to where a symbol (class, function, type, …) is defined, by exact name.
- **find_references** — where a symbol is used; semantic for C# and C/C++, lexical elsewhere.
- **search_symbols** — fuzzy symbol-name search (case-insensitive substring).
- **reindex** — rebuild the index for the workspace from scratch.

This **complements** Codex's native search rather than replacing it: reach for CodeCompass first on a
large or unfamiliar codebase, and fall back to native tools when a query isn't code-structure-shaped.
