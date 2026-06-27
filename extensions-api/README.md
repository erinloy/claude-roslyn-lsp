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
| `IMcpToolExtension` | agent-callable MCP tools (`ConfigureServices` + `ToolTypes`) | MCP |
| `IDiagnosticExtension` | live state merged into a file's diagnostics | daemon |
| `IHoverExtension` | live state on hover | daemon |

For MCP tools: `ConfigureServices` registers the services your `[McpServerToolType]` classes inject (typically a singleton
client to the running system); reference `ModelContextProtocol` with `CopyLocalLockFileAssemblies=false` so the host's copy
is shared (keeps the tool-attribute identity matching).

See `samples/SampleRunningSystemExtension/` for a complete working example (reports the host process's own live stats as a
stand-in for a running system) — it is exercised by the extension-loading test.

Resilience: a missing assembly, a wrong type, or a system that's down is logged and skipped — never fatal to the LSP.
