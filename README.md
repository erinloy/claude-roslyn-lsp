# claude-roslyn-lsp

Roslyn's language server — `Microsoft.CodeAnalysis.LanguageServer`, the engine behind the C# Dev Kit — packaged as a
[Claude Code](https://code.claude.com) plugin, for C# and Visual Basic. It runs as one shared per-workspace server with
thin per-session clients, so several Claude Code instances share a single loaded solution instead of each loading its own.

It ships three things:

- the **LSP server** (diagnostics, go-to-definition, find-references, hover, rename, code actions, workspace symbols);
- a **refactoring MCP** (`roslyn-refactor`) for the mutating operations Claude Code's read-only LSP tool lacks, plus a `crlsp`
  command-line client of the same shared server;
- an **extension API** that lets a repo surface a *running system* through the LSP — see [Extensions](#extensions).

## Why it exists

Two problems with running a C# language server under Claude Code, and what this does about them.

**Build locks.** A language server loads Roslyn analyzers and source generators to do its work, which holds OS handles on
those DLLs. In a repo that builds its own analyzers or generators, the next build can't overwrite the locked output —
`MSB3027`, "being used by another process". The fix the IDEs use is shadow-copying: load a *copy* of the analyzers from a
temp directory so the build output stays free. The Roslyn server does this by default; `csharp-ls` does not and can't be
configured to. This plugin extends the same idea to its *own* binaries: clients and the daemon run from a per-build shadow
copy, so rebuilding the plugin never blocks on a running instance either.

**One solution per instance.** A language server loads the whole solution into memory. Run one per editor instance and you
pay that cost N times. This plugin runs a single daemon per workspace and connects each Claude Code instance to it as a thin
client, so N instances cost roughly 1× the memory, not N×.

## Architecture

```
Claude #1 ─ client ─┐
Claude #2 ─ client ─┤  Sluice shared memory   ┌─ one daemon per workspace
Claude #3 ─ client ─┴───────────────────────▶ │   └─ one Microsoft.CodeAnalysis.LanguageServer
                                               │       (solution loaded once → 1× memory)
```

- **client** (`ClaudeRoslynLsp.Client`) — spawned by Claude Code per session. It derives a per-workspace key, connects to
  the daemon (electing to start one, under a named mutex, if none is running), and forwards messages between Claude Code's
  stdio and the daemon. It also routes files that live in a *different* repo to that repo's own daemon, and exits cleanly if
  its daemon dies so Claude Code reconnects.
- **daemon** (`ClaudeRoslynLsp.Daemon`) — one per workspace. It downloads and caches
  `Microsoft.CodeAnalysis.LanguageServer`, starts it, opens the solution, and multiplexes every client onto that one server:
  a shared `initialize`, id-namespaced requests, reference-counted document opens, and per-client diagnostic routing so one
  instance's edits don't flood the others. It shuts down after a period with no clients.

The client↔daemon hop uses [Sluice](https://github.com/erinloy/Sluice) shared-memory channels rather than a named pipe.

Both build from source on first run — the only download is Microsoft's server package. Claude Code spawns the components
through `boot/run.ps1`, which runs them from a shadow copy of the latest build. Building writes to `bin`, which is never
executed directly, so a rebuild can't be blocked by a running instance no matter how many are active.

## Requirements

- **.NET 10 SDK** on `PATH` (`dotnet --version` reports `10.x`). The client and daemon target `net8.0` with `RollForward`
  and run on the net10 runtime, so no separate .NET 8 install is needed.
- **The Claude Code LSP tool must be enabled** — see [Enabling the LSP tool](#enabling-the-lsp-tool). The refactoring MCP and
  the `crlsp` CLI work without it; only the in-editor LSP server depends on it.

## Install

From a running `claude`:

```
/plugin marketplace add erinloy/claude-roslyn-lsp
/plugin install roslyn@claude-roslyn-lsp
```

Restart Claude Code. The first launch builds the components and downloads the server (one-time, a minute or two); later
launches connect to the running daemon immediately. If you have more than one C# LSP plugin installed, disable the others so
only one claims `.cs`.

## Enabling the LSP tool

Claude Code's built-in `LSP` tool — the part that actually spawns a registered `.cs` server and surfaces its diagnostics and
navigation — is gated behind a client patch, tweakcc's `fix-lsp-support`. Without it the server is registered but never
launched, and `ENABLE_LSP_TOOL=1` alone is not enough on current builds. Claude Code auto-updates replace the binary and
revert the patch, so it has to be re-applied after each update. This gates every LSP plugin (gopls, pyright, OmniSharp,
roslyn) the same way; it is not specific to this one.

The plugin tracks the dependency for you:

- The `SessionStart` hook runs [`boot/lsp-patch.ps1 -Mode check`](boot/lsp-patch.ps1), which is version-keyed and warns when a
  Claude Code update has reverted the patch.
- `boot/lsp-patch.ps1 -Mode apply` installs tweakcc (or uses `npx`) and applies the patch. Windows locks the running
  `claude.exe`, so the patch can only land with no Claude process running; the script detects running instances and refuses
  cleanly rather than corrupting the binary.

  ```powershell
  # with every Claude session closed:
  pwsh -NoProfile -File boot/lsp-patch.ps1 -Mode apply
  ```

- To re-apply automatically after updates, register a per-user scheduled task that runs in the next all-closed window:

  ```powershell
  pwsh -NoProfile -File boot/install-lsp-autopatch.ps1
  ```

Background: [Piebald-AI/claude-code-lsps](https://github.com/Piebald-AI/claude-code-lsps) and
[tweakcc](https://github.com/Piebald-AI/tweakcc). Claude Code 2.0.74+ ships the LSP tool; the patch makes it usable.

## Refactoring MCP (`roslyn-refactor`)

A second component, shipped in the same plugin via `.mcp.json`, exposes Roslyn operations as MCP tools. It drives the shared
daemon over Sluice — it holds no workspace of its own, so it adds no per-agent memory. The mutating tools are the reason it
exists; Claude Code's LSP tool is read-only.

| Tool | Does |
|---|---|
| `rename_symbol(file, line, character, newName)` | Rename the symbol at a 0-based position solution-wide; write the edits to disk. |
| `rename_symbol_by_name(fullyQualifiedName, newName)` | Rename a type or namespace by qualified name. |
| `apply_code_action(file, line, character, title)` | Apply a Roslyn code action (a fix or refactoring) and write the result. |
| `organize_imports(file)` | Remove and sort `using`/`Imports` directives. |
| `format_document(file)` | Reformat with Roslyn's formatter. |
| `find_references` / `go_to_definition` / `find_implementations` / `type_definition` | Navigate by symbol. |
| `hover` / `document_symbols` / `search_symbols` / `list_code_actions` / `get_diagnostics` | Inspect a file or the workspace. |

Renames use Roslyn the way the IDE's "Rename" does: every binding occurrence updates across projects, and look-alike text in
strings and comments is left alone. Set `CLAUDE_ROSLYN_SOLUTION` to scope a large monorepo to one subsystem. See the
`roslyn-refactoring` skill for the workflow.

`cli/` builds the same operations into a `crlsp` command (`refs`, `rename`, `symbol`, `def`, `hover`, `diag`, `format`, …),
a command-line client of the same shared daemon — useful for scripting or driving the workspace outside an editor.

## Extensions

A repo can teach the server about a **running system**, not just its source. An extension dials out to a live process and
surfaces it through the same LSP and MCP surfaces an agent already uses:

- contribute **MCP tools** that query or act on the running system;
- contribute **workspace symbols** so `workspaceSymbol` finds the system's catalog alongside code;
- merge live state into a file's **diagnostics** or **hover**;
- push change events through the daemon's per-client routing.

Extensions are declared in `.claude-roslyn/extensions.json` in the consuming repo and loaded at startup; the plugin itself
stays system-agnostic. The daemon hot-reloads a rebuilt extension in place — no restart — and loads from a shadow copy so the
extension dll is never locked. Full contract and a worked example: [extensions-api/README.md](extensions-api/README.md).

## Configuration

| Variable | Effect |
|---|---|
| `CLAUDE_ROSLYN_SOLUTION` | Solution or project to open (absolute, or relative to the workspace root). Set this in a multi-solution repo, e.g. `src/App.slnx`. Otherwise the daemon picks the `.slnx`/`.sln` at or one level under the root. |
| `CLAUDE_ROSLYN_VERSION` | Pin an exact Roslyn server version instead of the latest published. |
| `CLAUDE_ROSLYN_SERVER_PATH` | Use an already-extracted `Microsoft.CodeAnalysis.LanguageServer.dll` and skip the download. |
| `CLAUDE_ROSLYN_MULTI_REPO` | Set to `0` to disable cross-repo routing (each foreign file then loads in the home daemon as a loose file). |

## Running by hand

```bash
# build everything once (what the SessionStart hook runs); re-run after editing source
pwsh -NoProfile -File boot/ensure-built.ps1 -Root .

# run a client (it starts the daemon if needed); set the workspace via env
CLAUDE_ROSLYN_WORKSPACE_ROOT=/path/to/repo dotnet exec client/bin/Release/net8.0/ClaudeRoslynLsp.Client.dll

# run the daemon directly (ConsoleAppFramework CLI — see --help)
dotnet exec daemon/bin/Release/net8.0/ClaudeRoslynLsp.Daemon.dll --root /path/to/repo

# print the server's runtime capabilities (providers, code-action kinds, semantic-token legend)
dotnet run --project bridge/ClaudeRoslynLsp.Bridge.csproj -- --capabilities
```

## Layout

```
.claude-plugin/plugin.json        plugin manifest
.claude-plugin/marketplace.json   marketplace descriptor (install from git)
.lsp.json / .mcp.json             LSP and MCP launch config (→ boot/run.ps1)
hooks/hooks.json                  SessionStart: ensure-built, lsp-patch check, lsp banner
boot/
  run.ps1                         shadow-copy launcher (build → bin, run from a copy)
  ensure-built.ps1                mutex-guarded build-once of client/daemon/mcp/cli; prewarm
  lsp-patch.ps1                   check/apply the Claude Code LSP-tool patch
  lsp-banner.ps1                  SessionStart presence notice for the LSP tools
client/
  lsp-client.cs                   thin client: connect-or-start the daemon, forward stdio
  Router.cs                       route each file to the daemon that owns its repo
daemon/
  Program.cs                      acquire the server, multiplex clients, idle shutdown
  LspMultiplexer.cs               one server ↔ many clients; diagnostic routing; extensions
bridge/                           shared primitives (reused by client, daemon, mcp, cli)
  RoslynAcquirer.cs               download and cache the server package
  RoslynServer.cs                 start the server process
  SolutionLocator.cs              pick the .slnx/.sln/.csproj for a workspace
  RoslynDaemonClient.cs           request/response client over a connected channel
  RoslynOps.cs                    rename / references / format / symbols as structured ops
  DaemonConnector.cs              connect / liveness / spawn-election
  PipeKey.cs · JsonRpc.cs · LspEdits.cs · CapabilitiesProbe.cs
mcp/
  RefactorTools.cs                the MCP tools (over the shared daemon)
  DaemonSession.cs                the MCP's connection to the daemon
  ExtensionLifecycle.cs           load and run MCP-side extensions
cli/Program.cs                    the crlsp command-line client
extensions-api/                   the extension contract + loader (see its README)
samples/SampleRunningSystemExtension/   a worked extension example
skills/roslyn-refactoring/        when and how to use the refactoring tools
```

## License

MIT — see [LICENSE](LICENSE).
