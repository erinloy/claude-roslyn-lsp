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
| `find_references(filePath, line, character)` | List every reference to the symbol — **read-only**. Use it to preview the blast radius before a rename. |
| `format_document(filePath)` | Reformat a file with Roslyn's formatter and write it back. |

Renames and formatting **write the edits to disk** and report the changed files. After a rename, re-read affected files before further editing them.

## Recommended workflow

1. **Locate** the symbol — a position (file + 0-based line/char) is most reliable; a fully-qualified name works for types/namespaces.
2. **Preview** with `find_references` when the change is broad or risky.
3. **Rename** with `rename_symbol` (or `rename_symbol_by_name`).
4. **Verify** — the tool returns the changed files; re-read them, and build the affected subsystem.

## Scoping in a large repo (important)

The server loads a Roslyn workspace via MSBuild. In a monorepo, **do not let it open a 300-project umbrella solution** — set the environment variable **`CLAUDE_ROSLYN_SOLUTION`** to the specific subsystem solution (`.slnx`/`.sln`) or project (`.csproj`/`.vbproj`) you're working in, so only that graph loads (faster, less memory). This is the same variable the LSP bridge uses. Without it, the server discovers the nearest solution at/under the working directory.

The first call loads the workspace (slow — MSBuild evaluation); subsequent calls are warm. After a rename, the workspace reloads from disk so later operations see current text.

## Notes / limits

- **Languages:** C# and VB only (Roslyn). For the server's full runtime capability list, run the bridge with `--capabilities`.
- `rename_symbol_by_name` resolves **types and namespaces**; for members/locals use `rename_symbol` with a position.
- Renames change source on disk but do **not** rename files; review and rename files separately if a class rename should move its file.
