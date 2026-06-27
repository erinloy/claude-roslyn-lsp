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

- **`client/` (`ClaudeRoslynLsp.Client`)** — a tiny per-instance client that Claude Code spawns. It derives a per-workspace pipe key, connects to the shared daemon (starting it, via mutex election, if none is running), then pumps bytes between Claude's stdio and the daemon's named pipe.
- **`daemon/` (`ClaudeRoslynLsp.Daemon`)** — **one per workspace**, shared by every instance. It acquires `Microsoft.CodeAnalysis.LanguageServer.<rid>` from nuget.org (cached), spawns it, drives `solution/open`, and **multiplexes** many client sessions onto the one server (cached `initialize`, id-namespaced requests, ref-counted document opens). It idle-shuts-down when the last client leaves.

```
Claude #1 ⟶ dotnet exec ClaudeRoslynLsp.Client.dll ⟶┐
Claude #2 ⟶ dotnet exec ClaudeRoslynLsp.Client.dll ⟶┤ pipe   ┌─ ONE daemon (per workspace)
Claude #3 ⟶ dotnet exec ClaudeRoslynLsp.Client.dll ⟶┴──────▶ │   └─ ONE Microsoft.CodeAnalysis.LanguageServer
                                                             │       (workspace loaded ONCE → 1× memory)
```

The command Claude spawns is `dotnet` directly (not `bash a-script` — Claude Code's LSP/MCP host spawns the command with no shell, so a direct executable is required).

**Why `dotnet exec`, never `dotnet run`.** `dotnet run` *builds* on every launch. With many Claude instances launching the **same** shared plugin copy concurrently, they race to build and lock the **same** output DLL — and the loser dies with `MSB3027 / "The build failed."`, so its server never connects. (This is the same N-instances pathology the centralized daemon exists to avoid, reappearing at the build layer.) The cure is to **build once, then only ever execute**: a `SessionStart` hook runs [`boot/ensure-built.ps1`](boot/ensure-built.ps1), which builds the client, daemon, and MCP to their Release DLLs **once, under a machine-wide mutex** (concurrent first-launches collapse to a single build; the rest wait, then find the DLL already there). Every launch after that is a pure `dotnet exec` of the prebuilt DLL — instant, lock-free, and collision-free no matter how many instances start at once.

## Requirements

- **.NET 10 SDK** on `PATH` (`dotnet --version` → `10.x`). Builds the client + daemon and runs the refactoring MCP's in-process MSBuild. (The client/daemon target `net8.0` with `RollForward` and run on the net10 runtime, so no separate .NET 8 install is needed.)
- **The Claude Code LSP tool must be patched in** — see [Enabling the LSP tool](#enabling-the-lsp-tool-setup-dependency) below. (Only the *LSP server* needs this; the refactoring MCP works without it.)

## Install

From a running `claude`:

```
/plugin marketplace add erinloy/claude-roslyn-lsp
/plugin install roslyn@claude-roslyn-lsp
```

(or `/plugin marketplace add https://github.com/erinloy/claude-roslyn-lsp.git`)

Then restart Claude Code. **First launch builds the client + daemon and downloads the server (one-time, ~1–2 min);** subsequent launches connect to the already-running daemon instantly. If you run more than one C# LSP plugin, disable the others so only one claims `.cs`.

## Enabling the LSP tool (setup dependency)

Claude Code's builtin **`LSP` tool** (the thing that actually *spawns* a registered `.cs` language server and surfaces diagnostics / go-to-def / find-refs) is gated behind a client patch — tweakcc's **`fix-lsp-support`**. Without it, the server is registered but **never launched**, no matter how correct the plugin is, and `ENABLE_LSP_TOOL=1` alone is not sufficient on current builds. Worse, **Claude Code auto-updates replace the binary and silently revert the patch**, so it must be re-applied after every update. (This gates *every* LSP plugin — gopls, pyright, OmniSharp, roslyn — identically; it is not roslyn-specific.)

The plugin owns this dependency so you don't have to track it by hand:

- **It re-checks every session.** The `SessionStart` hook runs [`boot/lsp-patch.ps1 -Mode check`](boot/lsp-patch.ps1), which is **version-keyed** — so after any CC auto-update it warns again that the patch needs re-applying.
- **It applies the patch for you, safely.** `boot/lsp-patch.ps1 -Mode apply` installs tweakcc (or uses `npx`) and applies `fix-lsp-support`. Windows locks the *running* `claude.exe`, so the patch can only land when **no Claude process is running** — the script detects running instances and refuses cleanly (no `EBUSY` crash) rather than corrupting the binary. In a multi-agent setup, apply it during an **all-sessions-closed window**:

  ```powershell
  # with every Claude session closed:
  pwsh -NoProfile -File boot/lsp-patch.ps1 -Mode apply
  # then relaunch Claude — the C# LSP starts on first use
  ```

- **Optional hands-free re-apply across updates.** Run once to register a per-user scheduled task that applies the patch in the next all-closed window after each update (no-op while Claude runs or already patched):

  ```powershell
  pwsh -NoProfile -File boot/install-lsp-autopatch.ps1
  ```

Background: [Piebald-AI/claude-code-lsps](https://github.com/Piebald-AI/claude-code-lsps) and [tweakcc](https://github.com/Piebald-AI/tweakcc). Claude Code 2.0.74+ ships the LSP tool; the patch makes it usable.

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
# build everything once (what the SessionStart hook runs); re-run any time after editing source
pwsh -NoProfile -File boot/ensure-built.ps1 -Root .

# run the thin client (it starts the daemon if needed); set the workspace via env
CLAUDE_ROSLYN_WORKSPACE_ROOT=/path/to/repo dotnet exec client/bin/Release/net8.0/ClaudeRoslynLsp.Client.dll

# run the daemon directly (ConsoleAppFramework CLI — see --help)
dotnet exec daemon/bin/Release/net8.0/ClaudeRoslynLsp.Daemon.dll --root /path/to/repo

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
.lsp.json              # LSP config → dotnet exec client/…/ClaudeRoslynLsp.Client.dll
.mcp.json              # refactoring MCP config → dotnet exec mcp/…/ClaudeRoslynLsp.Mcp.dll
hooks/
  hooks.json           # SessionStart → boot/ensure-built.ps1 (build-once, serialized)
boot/
  ensure-built.ps1     # mutex-guarded build of client+daemon+mcp to Release DLLs (skip-if-fresh)
skills/
  roslyn-refactoring/  # when/how to use the refactoring tools
client/
  ClaudeRoslynLsp.Client.csproj
  lsp-client.cs        # thin client: connect-or-start daemon, pipe⟷stdio (exec'd, never run)
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
