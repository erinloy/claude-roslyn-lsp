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

            // 🩸 THE CWD IS A STARTING POINT, NOT A ROOT — AND THIS PATH WAS MISSED WHEN THAT WAS FIXED ELSEWHERE.
            // 186c5e3/46e6b77 taught client/lsp-client.cs to walk up from the cwd to the repository that owns it, so
            // two sessions in one repo share one daemon instead of paying for a full Roslyn instance each. THIS site —
            // the one the MCP host actually connects (and therefore SPAWNS) daemons through — kept `?? cwd` raw. So the
            // fix covered the LSP client and not the surface every agent in this fleet actually uses.
            //
            // 📏 MEASURED 2026-09-09, and it is what the fleet spent the night looking at: five language servers, ~7.6 GB
            // of commit between them, on a box whose deploy gate wants 6 GB free. Three were rooted at NON-REPOSITORIES —
            // C:\SOURCE\scratch\___\echo\wt-head\src\Reactive.Graph (a subdirectory, 1.3 GB), a scratch dir, a Temp dir.
            // @ziltch2 found the hole by reading this line's sibling; @blackmagic's process table is what made it
            // findable; I had QUOTED the client-side line while answering that the walk-up was "already in it" and did
            // not notice this one.
            //
            // ⚖️ AN EXPLICIT ROOT IS STILL VERBATIM. CLAUDE_ROSLYN_WORKSPACE_ROOT is an operator's override and must not
            // be second-guessed — same contract as lsp-client.cs:48. Only the DEFAULT changes, from "wherever this
            // process happened to start" to "the repository that owns it", and when nothing above is a repository
            // ResolveWorkspaceRoot answers the starting directory, so a loose-files workspace behaves as it does today.
            string? explicitRoot = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT");
            string cwd = Directory.GetCurrentDirectory();
            string root = explicitRoot is { Length: > 0 } ? explicitRoot : PipeKey.ResolveWorkspaceRoot(cwd);
            if (explicitRoot is not { Length: > 0 } && !string.Equals(root, cwd, StringComparison.OrdinalIgnoreCase))
                _log($"workspace root: {root}  (walked up from cwd {cwd} — one daemon per REPOSITORY, not per directory)");
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

    /// <summary>Drop the cached connection so the next call reconnects (and restarts the daemon if it is gone).</summary>
    /// <remarks>Without this, one wedged or closed channel poisons the whole MCP session: the client is cached for the
    /// process lifetime, so every later tool call would keep failing against the same dead link. A bounded call that
    /// leaves a poisoned connection behind has only turned one hang into an unending series of errors.</remarks>
    public void Invalidate()
    {
        RoslynDaemonClient? dead = Interlocked.Exchange(ref _client, null);
        if (dead is null) return;
        _log("dropping the daemon connection — the next call will reconnect");
        _ = Task.Run(async () => { try { await dead.DisposeAsync().ConfigureAwait(false); } catch { } });
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { } c) await c.DisposeAsync().ConfigureAwait(false);
    }
}
