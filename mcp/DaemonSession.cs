using ClaudeRoslynLsp.Bridge;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// Holds this process's ONE connection to the shared Roslyn daemon for the workspace. The refactor tools drive that
/// shared workspace through LSP requests (references / rename / formatting / workspace-symbol) instead of each agent's
/// MCP loading its own MSBuildWorkspace — so N agents cost 1× the solution, not N×. The daemon is the same one the LSP
/// uses, so LSP and refactoring share a single warm Roslyn workspace.
/// </summary>
public sealed class DaemonSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _log = s => Console.Error.WriteLine($"[mcp] {s}");
    private RoslynDaemonClient? _client;

    /// <summary>Get the connected daemon client, connecting (and starting the daemon if needed) on first use.</summary>
    public async Task<RoslynDaemonClient> GetAsync(CancellationToken ct)
    {
        if (_client is { } live) return live;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { } stillLive) return stillLive;

            string root = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT") ?? Directory.GetCurrentDirectory();
            string? solution = Environment.GetEnvironmentVariable(SolutionLocator.OverrideEnvVar);
            // CLAUDE_PLUGIN_ROOT is set by Claude for the MCP; fall back to deriving it from this assembly's location
            // (mcp/bin/Release/net10.0 → up 4 = plugin root) so a manual run still finds the daemon DLL to start.
            string pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT")
                ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

            _log($"connecting to shared Roslyn daemon (root={root}) …");
            _client = await RoslynDaemonClient.ConnectAsync(root, solution, pluginRoot, _log, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("could not reach or start the shared Roslyn daemon");
            _log("connected to shared Roslyn daemon");
            return _client;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { } c) await c.DisposeAsync().ConfigureAwait(false);
    }
}
