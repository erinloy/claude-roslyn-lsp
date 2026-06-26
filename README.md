# claude-roslyn-lsp

The **gold-standard Roslyn language server** — the same engine Visual Studio 2026 / C# Dev Kit run (`Microsoft.CodeAnalysis.LanguageServer`) — as a [Claude Code](https://code.claude.com) LSP plugin, for **C# and Visual Basic**.

## Why this exists

Claude Code's C# LSP options (`csharp-ls`, OmniSharp) each load Roslyn analyzers and **source generators in place**, holding an OS handle on their DLLs. In a repo that **builds its own source generators** (Roslyn analyzers, incremental generators), the next build can't overwrite the locked DLL — `MSB3027 / "being used by another process"`. The standard cure, used by Visual Studio, Rider, and the Roslyn LSP, is **analyzer assembly shadow-copying**: load a *copy* into a temp dir so the build output stays free. `csharp-ls` doesn't do this and can't be configured to; the gold-standard Roslyn server does it by default.

This plugin brings that server to Claude Code, so you get:

- **No build locks** — analyzers/generators are shadow-copied; rebuild your generator with the LSP running.
- **Centralized — one server, shared.** A single per-workspace daemon owns the Roslyn workspace; every Claude instance is a thin client onto it. N instances cost **1× memory, not N×** (the per-instance-server blowup of `csharp-ls` / OmniSharp).
- **Native `.slnx`** (the modern XML solution format) **and .NET 10**.
- **C# *and* VB.NET** (`.cs`, `.csx`, `.vb`).
- The full Roslyn feature set: diagnostics, code fixes/refactorings, rename, hover, go-to-def/impl, find-references, call hierarchy, workspace symbols.
- A **Roslyn-powered refactoring MCP** (`roslyn-refactor`): solution-wide `rename_symbol` / `rename_symbol_by_name` / `find_references` / `format_document` — the mutating operations Claude Code's read-only LSP tool doesn't have.
- **Runtime capability interrogation** — ask the server what it actually exposes (`--capabilities`).

## How it works

The Roslyn server is **not standalone** — it expects a client (the C# Dev Kit) to tell it which workspace to load via a custom `solution/open` notification, and it ships only as platform NuGet packages. And running one server per editor instance loads the whole solution into memory once *per instance*. This plugin solves both with an **aspire-style client/server split**, built from source on your device (no prebuilt binary; the only download is Microsoft's server package):

- **`client/lsp-client.cs`** — a .NET 10 **file-based app** (`dotnet run --file`) that Claude Code spawns per instance. It is tiny: it derives a per-workspace pipe key, connects to the shared daemon (starting it, via mutex election, if none is running), then pumps bytes between Claude's stdio and the daemon's named pipe.
- **`daemon/` (`ClaudeRoslynLsp.Daemon`)** — **one per workspace**, shared by every instance. It acquires `Microsoft.CodeAnalysis.LanguageServer.<rid>` from nuget.org (cached), spawns it, drives `solution/open`, and **multiplexes** many client sessions onto the one server (cached `initialize`, id-namespaced requests, ref-counted document opens). It idle-shuts-down when the last client leaves.

```
Claude #1 ⟶ dotnet run --file lsp-client.cs ⟶┐
Claude #2 ⟶ dotnet run --file lsp-client.cs ⟶┤ named pipe   ┌─ ONE daemon (per workspace)
Claude #3 ⟶ dotnet run --file lsp-client.cs ⟶┴────────────▶ │   └─ ONE Microsoft.CodeAnalysis.LanguageServer
                                                            │       (workspace loaded ONCE → 1× memory)
```

The command Claude spawns is `dotnet` directly (not `bash a-script` — Claude Code's LSP/MCP host spawns the command with no shell, so a direct executable is required), and `.NET 10`'s file-based apps keep stdout clean for the JSON-RPC wire.

## Requirements

- **.NET 10 SDK** on `PATH` (`dotnet --version` → `10.x`). Required for the file-based-app client (`dotnet run --file`) and for the refactoring MCP's in-process MSBuild.

## Install

From a running `claude`:

```
/plugin marketplace add erinloy/claude-roslyn-lsp
/plugin install roslyn@claude-roslyn-lsp
```

(or `/plugin marketplace add https://github.com/erinloy/claude-roslyn-lsp.git`)

Then restart Claude Code. **First launch builds the client + daemon and downloads the server (one-time, ~1–2 min);** subsequent launches connect to the already-running daemon instantly. If you run more than one C# LSP plugin, disable the others so only one claims `.cs`.

> Claude Code's built-in LSP tool may need enabling — see [Piebald-AI/claude-code-lsps](https://github.com/Piebald-AI/claude-code-lsps) (`npx tweakcc --apply`) and Claude Code 2.1.50+.

## Configuration (env vars)

| Variable | Effect |
|---|---|
| `CLAUDE_ROSLYN_SOLUTION` | Pin the solution/project to open (absolute, or relative to the workspace root) — set this in a **multi-solution repo** (e.g. `src/App.slnx`). Otherwise the bridge picks the `.slnx`/`.sln` at (or one level under) the root, deterministically. |
| `CLAUDE_ROSLYN_VERSION` | Pin an exact Roslyn server version instead of the latest published. |
| `CLAUDE_ROSLYN_SERVER_PATH` | Use an already-extracted `Microsoft.CodeAnalysis.LanguageServer.dll`, skipping the download entirely. |

## Refactoring MCP (`roslyn-refactor`)

A second built-from-source .NET component, shipped in the same plugin via `.mcp.json`, exposes **mutating** Roslyn operations as MCP tools (the LSP tool only does reads):

| Tool | Does |
|---|---|
| `rename_symbol(filePath, line, character, newName)` | Rename the symbol at a 0-based position **solution-wide**, write edits to disk. |
| `rename_symbol_by_name(fullyQualifiedName, newName)` | Rename a type/namespace by qualified name. |
| `find_references(filePath, line, character)` | List every reference (read-only). |
| `format_document(filePath)` | Reformat with Roslyn's formatter. |

These use Roslyn as a library (`MSBuildWorkspace` + `Renamer` + `SymbolFinder`) — the same engine as the IDE's "Rename", so every reference across projects updates and look-alike text in strings/comments is left alone. It honors **`CLAUDE_ROSLYN_SOLUTION`** for scoping (set it to a subsystem `.slnx` in a big monorepo). `.slnx` is parsed directly (MSBuildWorkspace can't read the XML format). See the `roslyn-refactoring` skill for the workflow.

> Requires the **.NET 10 SDK** (the MCP loads the SDK's in-process MSBuild via MSBuildLocator).

## Runtime capabilities

Roslyn covers **C# and VB**, but its capability/extension surface is version-dependent — interrogate it:

```bash
dotnet run --project bridge/ClaudeRoslynLsp.Bridge.csproj -- --capabilities
```

prints the providers, code-action kinds, executable commands, and semantic-token legend the server actually advertises (raw capabilities JSON is the last block on stdout).

## Run / debug by hand

```bash
# run the thin client (it starts the daemon if needed); set the workspace via env
CLAUDE_ROSLYN_WORKSPACE_ROOT=/path/to/repo dotnet run --file client/lsp-client.cs

# run the daemon directly (ConsoleAppFramework CLI — see --help)
dotnet run --project daemon/ClaudeRoslynLsp.Daemon.csproj -- --root /path/to/repo

# interrogate the server's runtime capabilities
dotnet run --project bridge/ClaudeRoslynLsp.Bridge.csproj -- --capabilities

# run the refactoring MCP server directly (stdio MCP)
dotnet run --project mcp/ClaudeRoslynLsp.Mcp.csproj
```

## Layout

```
.claude-plugin/
  plugin.json          # plugin manifest
  marketplace.json     # marketplace descriptor (install from git)
.lsp.json              # LSP config → dotnet run --file client/lsp-client.cs
.mcp.json              # refactoring MCP config → dotnet run --project mcp/…
skills/
  roslyn-refactoring/  # when/how to use the refactoring tools
client/
  lsp-client.cs        # file-based thin client: connect-or-start daemon, pipe⟷stdio
daemon/                # the shared per-workspace server (ConsoleAppFramework CLI)
  Program.cs           # acquire LS → multiplex clients → idle-shutdown
  LspMultiplexer.cs    # one LS ⟷ many client sessions (id-namespacing, doc refcount)
bridge/                # reused primitives + the --capabilities / --download tool
  RoslynAcquirer.cs    # download + cache the Roslyn server package
  RoslynServer.cs      # spawn the server (apphost else dotnet <dll>)
  SolutionLocator.cs   # pick .slnx/.sln/.csproj for the workspace
  PipeKey.cs           # per-workspace pipe name (client replicates it)
  CapabilitiesProbe.cs # --capabilities handshake + summary
  LspStdioProxy.cs     # legacy single-process stdio proxy (still used by --capabilities path)
  JsonRpc.cs           # Content-Length framing read/write
mcp/                   # the refactoring MCP server (built on device)
  WorkspaceHost.cs     # warm MSBuildWorkspace (+ .slnx project loader)
  RefactorTools.cs     # rename / find-references / format tools
```

## License

MIT — see [LICENSE](LICENSE).
