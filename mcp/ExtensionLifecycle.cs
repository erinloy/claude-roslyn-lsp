using ClaudeRoslynLsp.Extensions;
using Microsoft.Extensions.Hosting;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>The workspace root the extensions were loaded for (so the lifecycle can build their <see cref="ExtensionContext"/>).</summary>
public sealed record ExtensionWorkspace(string Root);

/// <summary>
/// Drives the loaded extensions' lifecycle in the MCP host: InitializeAsync (dial out to the running system) on start,
/// DisposeAsync on shutdown. Failures are isolated and logged — one extension that can't reach its system must never take
/// the MCP server down.
/// </summary>
public sealed class ExtensionLifecycle : IHostedService
{
    private readonly IReadOnlyList<LoadedExtension> _extensions;
    private readonly string _root;

    public ExtensionLifecycle(IReadOnlyList<LoadedExtension> extensions, ExtensionWorkspace workspace)
    {
        _extensions = extensions;
        _root = workspace.Root;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (LoadedExtension le in _extensions)
        {
            void Log(string m) => Console.Error.WriteLine($"[ext:{le.Name}] {m}");
            try
            {
                await le.Extension.InitializeAsync(
                    new ExtensionContext { WorkspaceRoot = _root, Host = "mcp", Log = Log, Config = le.Config }, ct)
                    .ConfigureAwait(false);
                Log("initialized");
            }
            catch (Exception ex) { Log($"InitializeAsync failed (tools will still load, may return errors until reachable): {ex.Message}"); }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (LoadedExtension le in _extensions.Reverse())
            try { await le.Extension.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
