# claude-roslyn-lsp extensions

Repo-declared extensions let an agent see a **running system**, not just the static codebase. The open-source LSP stays
system-agnostic; everything system-specific (the endpoint, the protocol, the data) lives in an extension assembly the repo
points at, and the extension **dials out** to the running system itself.

## Manifest

Create `.claude-roslyn/extensions.json` at the workspace root:

```json
{
  "extensions": [
    {
      "name": "matrix",
      "assembly": "src/Matrix/Matrix.LspExtension/bin/Release/net8.0/Matrix.LspExtension.dll",
      "type": "Matrix.LspExtension.MatrixExtension",
      "enabled": true,
      "config": { "endpoint": "http://localhost:5077" }
    }
  ]
}
```

- `assembly` — path to the extension dll, relative to the workspace root (or absolute).
- `type` — the `ICrlspExtension` implementation's full name.
- `config` — passed verbatim to the extension as `ExtensionContext.Config` (put your endpoint/auth here).
- `enabled` — set `false` to keep an entry without loading it.

## Authoring

Reference `ClaudeRoslynLsp.Extensions.Abstractions` and target **net8.0** (so the one build loads into both hosts: the
net8.0 LSP daemon and the net10.0 MCP server). Because you dial out, you don't need the running system's in-process
libraries — just an HTTP/Sluice/pipe client.

Implement `ICrlspExtension` (lifecycle) plus any capability interface for the surfaces you want:

| Interface | Surface | Loaded by |
|---|---|---|
| `ICrlspExtension` | `InitializeAsync` (dial out) / `DisposeAsync` | daemon + MCP |
| `IMcpToolExtension` | agent-callable MCP tools — **QUERIES** (`ConfigureServices` + `ToolTypes`) | MCP |
| `ISymbolExtension` | the running system's catalog reachable via `workspaceSymbol` — **SYMBOLS** | daemon |
| `IDiagnosticExtension` | live state merged into a file's diagnostics | daemon |
| `IHoverExtension` | live state on hover | daemon |

### Subscribable streams

For **STREAMS** — pushing the running system's change-events to the agent — there is no separate interface. In
`InitializeAsync`, start your own subscription to the running system (e.g. an SSE/WebSocket watch), and on each change call
`context.RequestDiagnosticRefresh(uri)` (or `(null)` for every open document). The daemon re-pulls and re-publishes those
documents' diagnostics — now carrying your updated `IDiagnosticExtension` state — through its existing per-client routing, so
each agent sees the change on exactly the files it has open. The callback is daemon-host only (`null` under MCP) — null-check it.

The three live capabilities compose: a `workspaceSymbol` hit's `LocationUri` (SYMBOLS) is a provider URI your query tool
(QUERIES) reads back, and a stream tick (STREAMS) re-surfaces the new value wherever it's shown.

For MCP tools: `ConfigureServices` registers the services your `[McpServerToolType]` classes inject (typically a singleton
client to the running system); reference `ModelContextProtocol` with `CopyLocalLockFileAssemblies=false` so the host's copy
is shared (keeps the tool-attribute identity matching).

See `samples/SampleRunningSystemExtension/` for a complete working example (reports the host process's own live stats as a
stand-in for a running system) — it is exercised by the extension-loading test.

## Updating an extension without restarting

Two reasons you rarely need to restart anything:

1. **The extension dials out, so most changes need no rebuild at all.** New organs, programs, or data in the running system
   flow through live — `workspaceSymbol` and the query tools fetch the catalog/data from the system on each call. You only
   rebuild the extension when the *bridge code* changes (a new tool, a changed capability mapping).

2. **When you do rebuild, the dll is never locked.** Both hosts load the extension from a **shadow copy**, so you can rebuild
   `Matrix.LspExtension.dll` in place while the daemon and MCP are running — no "stop the host first".

What a rebuild does, per surface:

- **Daemon side (SYMBOLS / diagnostics / hover): hot-reloaded in place.** The daemon watches your dll; on rebuild it swaps
  the new version into a fresh collectible load context and re-runs `InitializeAsync` — **no daemon restart, no CC restart**.
  Open documents re-publish so the new state shows immediately.
- **MCP side (tools / QUERIES): picked up on the next MCP start.** Tools register at MCP startup, so a rebuilt *tool surface*
  takes effect when the MCP server next starts (a `/mcp` reconnect or a new session) — still **no full CC restart**, and the
  dll was never locked so the rebuild itself is free. (Most iteration is daemon-side or dial-out, so this is the rare case.)

Resilience: a missing assembly, a wrong type, or a system that's down is logged and skipped — never fatal to the LSP. A
hot-reload that fails (mid-write dll, bad build, init throw) keeps the previously-loaded version serving and logs the error.
