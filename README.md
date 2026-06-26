# claude-roslyn-lsp

The **gold-standard Roslyn language server** — the same engine Visual Studio 2026 / C# Dev Kit run (`Microsoft.CodeAnalysis.LanguageServer`) — as a [Claude Code](https://code.claude.com) LSP plugin, for **C# and Visual Basic**.

## Why this exists

Claude Code's C# LSP options (`csharp-ls`, OmniSharp) each load Roslyn analyzers and **source generators in place**, holding an OS handle on their DLLs. In a repo that **builds its own source generators** (Roslyn analyzers, incremental generators), the next build can't overwrite the locked DLL — `MSB3027 / "being used by another process"`. The standard cure, used by Visual Studio, Rider, and the Roslyn LSP, is **analyzer assembly shadow-copying**: load a *copy* into a temp dir so the build output stays free. `csharp-ls` doesn't do this and can't be configured to; the gold-standard Roslyn server does it by default.

This plugin brings that server to Claude Code, so you get:

- **No build locks** — analyzers/generators are shadow-copied; rebuild your generator with the LSP running.
- **Native `.slnx`** (the modern XML solution format) **and .NET 10**.
- **C# *and* VB.NET** (`.cs`, `.csx`, `.vb`).
- The full Roslyn feature set: diagnostics, code fixes/refactorings, rename, hover, go-to-def/impl, find-references, call hierarchy, workspace symbols.

## How it works

The Roslyn server is **not standalone** — it expects a client (the C# Dev Kit) to tell it which workspace to load via a custom `solution/open` notification, and it ships only as platform NuGet packages. This plugin is a small **.NET bridge** that fills both gaps:

1. **Acquires** `Microsoft.CodeAnalysis.LanguageServer.<rid>` from nuget.org on first run (cached under the plugin data dir).
2. **Spawns** it over stdio and **proxies** all LSP traffic byte-for-byte.
3. After the client's `initialized`, **discovers** the `.slnx` / `.sln` (or loose `.csproj` / `.vbproj`) under the workspace root and sends the Roslyn `solution/open` (or `project/open`) notification — so the workspace actually loads.

The bridge is **built from source on your device** by the launcher (no prebuilt binary is shipped); the only dependency it downloads is Microsoft's server package.

```
client (Claude Code) ⟷ scripts/launch.sh ⟶ bridge (this repo) ⟷ Microsoft.CodeAnalysis.LanguageServer
                                              ├─ acquire server (nuget.org, cached)
                                              ├─ proxy LSP stdio
                                              └─ drive solution/open
```

## Requirements

- **.NET SDK 8.0+** on `PATH` (the bridge targets net8.0; this repo's own server runs on .NET 10 too). `dotnet --version` should work.
- **bash** on `PATH` — present everywhere; on Windows this is **Git Bash** (ships with Git for Windows), which Claude Code already uses.

## Install

From a running `claude`:

```
/plugin marketplace add erinloy/claude-roslyn-lsp
/plugin install roslyn@claude-roslyn-lsp
```

(or `/plugin marketplace add https://github.com/erinloy/claude-roslyn-lsp.git`)

Then restart Claude Code. **First launch builds the bridge and downloads the server (one-time, ~1–2 min);** subsequent launches are instant. If you run more than one C# LSP plugin, disable the others so only one claims `.cs`.

> Claude Code's built-in LSP tool may need enabling — see [Piebald-AI/claude-code-lsps](https://github.com/Piebald-AI/claude-code-lsps) (`npx tweakcc --apply`) and Claude Code 2.1.50+.

## Configuration (env vars)

| Variable | Effect |
|---|---|
| `CLAUDE_ROSLYN_SOLUTION` | Pin the solution/project to open (absolute, or relative to the workspace root) — set this in a **multi-solution repo** (e.g. `src/App.slnx`). Otherwise the bridge picks the `.slnx`/`.sln` at (or one level under) the root, deterministically. |
| `CLAUDE_ROSLYN_VERSION` | Pin an exact Roslyn server version instead of the latest published. |
| `CLAUDE_ROSLYN_SERVER_PATH` | Use an already-extracted `Microsoft.CodeAnalysis.LanguageServer.dll`, skipping the download entirely. |

## Run / debug by hand

```bash
# build + run the bridge directly (it logs to stderr and to <data>/bridge.log)
dotnet run --project bridge/ClaudeRoslynLsp.Bridge.csproj -- --stdio

# just pre-download the server
dotnet run --project bridge/ClaudeRoslynLsp.Bridge.csproj -- --download
```

## Layout

```
.claude-plugin/
  plugin.json          # plugin manifest
  marketplace.json     # marketplace descriptor (install from git)
.lsp.json              # LSP server config → scripts/launch.sh
scripts/launch.sh      # build-from-source (idempotent) then exec the bridge
bridge/                # the .NET bridge (built on device)
  Program.cs           # entry: acquire → proxy → drive solution/open
  RoslynAcquirer.cs    # download + cache the Roslyn server package
  LspStdioProxy.cs     # stdio proxy + solution/open injection
  SolutionLocator.cs   # pick .slnx/.sln/.csproj for the workspace
  JsonRpc.cs           # Content-Length framing read/write
```

## License

MIT — see [LICENSE](LICENSE).
