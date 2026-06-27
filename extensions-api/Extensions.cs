using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeRoslynLsp.Extensions;

/// <summary>
/// The contract a claude-roslyn-lsp extension implements. Extensions are declared in <c>.claude-roslyn/extensions.json</c>
/// at a workspace root and loaded by the LSP daemon and/or the MCP server. Their purpose is to give the agent visibility
/// into a RUNNING SYSTEM (not just the static codebase): an extension dials OUT to that system itself and surfaces it as
/// MCP tools, diagnostics, and hover. The open-source LSP stays entirely system-agnostic — every system-specific detail
/// (the endpoint, the protocol, the data shape) lives in the extension and its manifest <c>config</c> block.
///
/// Implement the opt-in capability interfaces alongside this one for each surface you want to contribute:
/// <see cref="IMcpToolExtension"/> (agent-callable tools), <see cref="IDiagnosticExtension"/> (live state as a file's
/// diagnostics), <see cref="IHoverExtension"/> (live state on hover).
/// </summary>
public interface ICrlspExtension : IAsyncDisposable
{
    /// <summary>Short, stable identifier (also the diagnostic source / log prefix).</summary>
    string Name { get; }

    /// <summary>Called once per host process at startup — establish the connection to the running system (dial out).
    /// Keep it resilient: a system that is down must not break the host; surface that as empty results / a clear message.</summary>
    Task InitializeAsync(ExtensionContext context, CancellationToken ct);
}

/// <summary>What the host hands an extension at load time.</summary>
public sealed class ExtensionContext
{
    /// <summary>The workspace root the manifest was found under (where <c>.claude-roslyn/extensions.json</c> lives).</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Which host loaded this instance: <c>"daemon"</c> (LSP) or <c>"mcp"</c>. Lets one extension behave per-surface.</summary>
    public required string Host { get; init; }

    /// <summary>Log sink (goes to the host's stderr/log file — never to a protocol wire).</summary>
    public required Action<string> Log { get; init; }

    /// <summary>The extension's own <c>config</c> object from its manifest entry (e.g. the running system's endpoint).</summary>
    public JsonObject? Config { get; init; }
}

/// <summary>Opt-in: contribute MCP tools. The host calls <see cref="ConfigureServices"/> into the MCP DI container, then
/// registers <see cref="ToolTypes"/> — so a tool class can inject the running-system client the extension registered.</summary>
public interface IMcpToolExtension
{
    /// <summary>Register the services the tool classes inject (typically a singleton client to the running system).</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>The <c>[McpServerToolType]</c> classes to register with the MCP server.</summary>
    IEnumerable<Type> ToolTypes { get; }
}

/// <summary>Opt-in: contribute diagnostics for a document from the running system — e.g. flag that the actuator a file
/// defines is live and breaching a limit. Merged into the file's normal Roslyn diagnostics by the daemon.</summary>
public interface IDiagnosticExtension
{
    Task<IReadOnlyList<ExtDiagnostic>> GetDiagnosticsAsync(string fileUri, string filePath, CancellationToken ct);
}

/// <summary>Opt-in: contribute hover text for a position from the running system — e.g. a symbol's live value/state.</summary>
public interface IHoverExtension
{
    Task<string?> GetHoverAsync(string fileUri, string filePath, int line, int character, CancellationToken ct);
}

/// <summary>A diagnostic an extension contributes (0-based positions). Severity mirrors LSP DiagnosticSeverity.</summary>
public readonly record struct ExtDiagnostic(
    int Line, int Character, int EndLine, int EndCharacter, ExtSeverity Severity, string Code, string Message);

public enum ExtSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }
