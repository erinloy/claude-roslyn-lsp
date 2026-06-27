using ClaudeRoslynLsp.Bridge;   // RoslynAcquirer, RoslynServer, PipeKey (linked)
using ClaudeRoslynLsp.Daemon;
using ConsoleAppFramework;
using Sluice;                   // ShmFrameListener — the client rendezvous (NamedPipeServerStream analogue)

// The shared Roslyn daemon — one per workspace, owns ONE Roslyn language server + workspace, multiplexed onto many thin
// clients over a named pipe. Launched (detached) by lsp-client.cs when no daemon for the workspace is yet running.
await ConsoleApp.RunAsync(args, RunDaemon);

/// <summary>Run the shared daemon.</summary>
/// <param name="root">Workspace root (the LS rootUri; also drives solution discovery). Defaults to the cwd.</param>
/// <param name="pipe">Named pipe to serve. Defaults to the key derived from the root (clients match it).</param>
/// <param name="solution">Explicit solution/project override. Defaults to CLAUDE_ROSLYN_SOLUTION, else discovery.</param>
/// <param name="idleSeconds">Shut down after this long with zero clients.</param>
/// <param name="ct">Wired by ConsoleAppFramework to Ctrl-C / SIGTERM.</param>
static async Task RunDaemon(
    string? root = null, string? pipe = null, string? solution = null, int idleSeconds = 600,
    CancellationToken ct = default)
{
    root ??= Directory.GetCurrentDirectory();
    string pipeName = pipe ?? PipeKey.ForRoot(root);
    string? solutionOverride = solution ?? Environment.GetEnvironmentVariable(SolutionLocator.OverrideEnvVar);

    string dataDir = ResolveDataDir();
    Directory.CreateDirectory(dataDir);
    string logFile = Path.Combine(dataDir, $"daemon-{pipeName}.log");
    object logGate = new();
    void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        lock (logGate)
        {
            Console.Error.WriteLine(line);
            try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { }
        }
    }

    // One daemon per pipe. If another already holds the lock, a sibling beat us to it — exit quietly.
    using var single = new Mutex(initiallyOwned: false, $"{pipeName}-daemon", out _);
    if (!single.WaitOne(0)) { Log($"another daemon already owns {pipeName}; exiting"); return; }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    try
    {
        Log($"daemon starting — root={root} pipe={pipeName} idle={idleSeconds}s");
        string logDir = Path.Combine(dataDir, "logs");
        string serverDll = await RoslynAcquirer.EnsureServerAsync(dataDir, Log, cts.Token).ConfigureAwait(false);

        using var server = RoslynServer.Start(serverDll, logDir, Log);
        Log($"roslyn server pid {server.Id} started");

        // Repo-declared extensions (.claude-roslyn/extensions.json), HOT-RELOADABLE: each lives in its own collectible load
        // context loaded from a shadow copy, and a rebuilt dll is swapped in at runtime — no daemon restart. The daemon
        // surfaces the running system they dial out to as diagnostics, hover, and workspaceSymbol-into-the-catalog (SYMBOLS).
        // The mux reads the CURRENT instances live via the accessors below, so a reload takes effect on the next request.
        ReloadableExtensionHost? extHost = null;
        var mux = new LspMultiplexer(server.StandardInput.BaseStream, server.StandardOutput.BaseStream, Log, cts.Token,
            () => extHost?.DiagnosticExtensions ?? Array.Empty<IDiagnosticExtension>(),
            () => extHost?.HoverExtensions ?? Array.Empty<IHoverExtension>(),
            () => extHost?.SymbolExtensions ?? Array.Empty<ISymbolExtension>());
        await mux.StartAsync(root, solutionOverride).ConfigureAwait(false);

        extHost = await ReloadableExtensionHost.StartAsync(root, "daemon", Log,
            contextFactory: (name, config) => new ExtensionContext
            {
                WorkspaceRoot = root, Host = "daemon", Log = Log, Config = config,
                RequestDiagnosticRefresh = uri => { mux.RefreshDiagnostics(uri); return Task.CompletedTask; },
            },
            onReloaded: () => mux.RefreshDiagnostics(null), // re-publish open docs so the reloaded extension's state surfaces
            ct: cts.Token).ConfigureAwait(false);

        var idle = new IdleShutdown(mux, TimeSpan.FromSeconds(idleSeconds), Log, cts);
        idle.Start();

        // Accept clients until cancelled or the LS dies.
        var accept = AcceptLoopAsync(pipeName, mux, Log, cts.Token);
        var serverExit = WaitForExitAsync(server, cts.Token);
        await Task.WhenAny(accept, serverExit).ConfigureAwait(false);

        cts.Cancel();
        if (extHost is not null) { try { await extHost.DisposeAsync().ConfigureAwait(false); } catch { } }
        try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch { }
        Log("daemon exiting");
    }
    catch (OperationCanceledException) { }
}

static async Task AcceptLoopAsync(string endpoint, LspMultiplexer mux, Action<string> log, CancellationToken ct)
{
    using var listener = new ShmFrameListener(endpoint, PipeKey.FrameCapacity);
    while (!ct.IsCancellationRequested)
    {
        try
        {
            // Accept blocks (spin→doorbell) on a pool thread; each connection is its own duplex frame channel.
            IFrameChannel channel = await Task.Run(() => listener.Accept(ct), ct).ConfigureAwait(false);
            mux.AddClient(channel);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { log($"accept error: {ex.Message}"); }
    }
}

static async Task WaitForExitAsync(System.Diagnostics.Process p, CancellationToken ct)
{
    try { await p.WaitForExitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
}

static string ResolveDataDir()
{
    string? fromPlugin = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA");
    if (!string.IsNullOrWhiteSpace(fromPlugin)) return Path.Combine(fromPlugin, "roslyn");
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-roslyn-lsp");
}

/// <summary>Exits the daemon after a continuous window with zero clients, so an idle workspace frees its memory.</summary>
sealed class IdleShutdown
{
    private readonly LspMultiplexer _mux;
    private readonly TimeSpan _timeout;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts;
    private DateTime _zeroSince = DateTime.UtcNow; // no clients have connected yet

    public IdleShutdown(LspMultiplexer mux, TimeSpan timeout, Action<string> log, CancellationTokenSource cts)
    { _mux = mux; _timeout = timeout; _log = log; _cts = cts; _mux.ClientCountChanged += () => { if (_mux.ClientCount > 0) _zeroSince = DateTime.MaxValue; else _zeroSince = DateTime.UtcNow; }; }

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                if (_mux.ClientCount == 0 && _zeroSince != DateTime.MaxValue && DateTime.UtcNow - _zeroSince > _timeout)
                {
                    _log($"idle for {_timeout.TotalSeconds:0}s with no clients — shutting down");
                    _cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
