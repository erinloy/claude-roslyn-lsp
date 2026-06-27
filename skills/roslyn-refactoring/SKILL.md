---
name: roslyn-refactoring
description: Use when renaming a C#/VB class, namespace, method, or any symbol across a whole .NET solution, or finding all references to one, or formatting a file — driven by the real Roslyn engine (the same one Visual Studio's "Rename" uses) via the roslyn-refactor MCP server. Prefer these over manual find-and-replace for any rename, because Roslyn updates every reference (including across projects) correctly and skips look-alike text in strings/comments.
---

# Roslyn-powered refactoring (C# / VB)

The `roslyn-refactor` MCP server performs **solution-wide, semantically-correct** code mutations using Roslyn — not text substitution. Use it whenever a change must follow symbol semantics across files and projects.

## Why not just edit / find-and-replace

A textual rename of `Order` hits `OrderLine`, `Reorder`, the word in a comment, and a string literal — and misses partial-class and cross-project references. Roslyn renames the **symbol**: every binding occurrence, across the whole loaded solution, and nothing else. Always prefer these tools for renames.

## Tools

| Tool | Use it to |
|---|---|
| `rename_symbol(filePath, line, character, newName)` | Rename the symbol at a **0-based** file position everywhere. Best when you have a location (e.g. from an LSP/grep hit). |
| `rename_symbol_by_name(fullyQualifiedName, newName)` | Rename a **type or namespace** by qualified name (e.g. `My.Ns.OldClass`, or a namespace `My.Old.Area`) when you don't have a position. |
| `apply_code_action(filePath, line, character, title)` | Apply a Roslyn code action (a fix or refactoring) at a position, selected by its title, and write the result. |
| `organize_imports(filePath)` | Remove unused and sort the file's `using`/`Imports` directives. |
| `find_references(filePath, line, character)` | List every reference to the symbol — **read-only**. Use it to preview the blast radius before a rename. |
| `format_document(filePath)` | Reformat a file with Roslyn's formatter and write it back. |

These tools **write the edits to disk** and report the changed files. After one runs, re-read affected files before editing them further. (The same MCP server also exposes read-only navigation — `go_to_definition`, `hover`, `document_symbols`, `search_symbols`, `get_diagnostics`, `list_code_actions` — which overlap with Claude Code's LSP tool; this skill is about the mutating operations.)

## Recommended workflow

1. **Locate** the symbol — a position (file + 0-based line/char) is most reliable; a fully-qualified name works for types/namespaces.
2. **Preview** with `find_references` when the change is broad or risky.
3. **Rename** with `rename_symbol` (or `rename_symbol_by_name`).
4. **Verify** — the tool returns the changed files; re-read them, and build the affected subsystem.

## Scoping in a large repo (important)

The tools run against the shared per-workspace daemon, which loads one Roslyn solution. In a monorepo, set **`CLAUDE_ROSLYN_SOLUTION`** to the specific subsystem solution (`.slnx`/`.sln`) or project (`.csproj`/`.vbproj`) you're working in, so the daemon scopes to that graph instead of a 300-project umbrella solution (faster, less memory). This is the same variable the LSP server uses. Without it, the daemon picks the nearest solution at or under the working directory.

The first call after a cold daemon start can return an empty result while the solution finishes loading and `workspaceSymbol`'s index builds; retry once it is warm. Operations act on the daemon's loaded view of the solution.

## Notes / limits

- **Languages:** C# and VB only (Roslyn). For the server's full runtime capability list, run the bridge with `--capabilities`.
- `rename_symbol_by_name` resolves **types and namespaces**; for members/locals use `rename_symbol` with a position.
- Renames change source on disk but do **not** rename files; review and rename files separately if a class rename should move its file.
