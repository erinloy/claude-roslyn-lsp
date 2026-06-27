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

        // Load repo-declared extensions (.claude-roslyn/extensions.json) and partition them by capability. The daemon
        // surfaces the running system they dial out to as: diagnostics merged into a file's set, hover, and workspaceSymbol
        // reaching into the system's catalog (SYMBOLS). Partition first (no init yet) so the mux can carry the capability
        // lists, then dial each extension out AFTER the mux exists — its STREAMS refresh callback rides the live mux.
        var loadedExtensions = ExtensionLoader.Load(root, "daemon", Log);
        var diagExtensions = new List<IDiagnosticExtension>();
        var hoverExtensions = new List<IHoverExtension>();
        var symbolExtensions = new List<ISymbolExtension>();
        foreach (var le in loadedExtensions)
        {
            if (le.Extension is IDiagnosticExtension d) diagExtensions.Add(d);
            if (le.Extension is IHoverExtension h) hoverExtensions.Add(h);
            if (le.Extension is ISymbolExtension s) symbolExtensions.Add(s);
        }

        var mux = new LspMultiplexer(server.StandardInput.BaseStream, server.StandardOutput.BaseStream, Log, cts.Token, diagExtensions, hoverExtensions, symbolExtensions);
        await mux.StartAsync(root, solutionOverride).ConfigureAwait(false);

        foreach (var le in loadedExtensions)
        {
            try
            {
                await le.Extension.InitializeAsync(new ExtensionContext
                {
                    WorkspaceRoot = root, Host = "daemon", Log = Log, Config = le.Config,
                    RequestDiagnosticRefresh = uri => { mux.RefreshDiagnostics(uri); return Task.CompletedTask; },
                }, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Log($"extension '{le.Name}' init failed: {ex.Message}"); }
        }

        var idle = new IdleShutdown(mux, TimeSpan.FromSeconds(idleSeconds), Log, cts);
        idle.Start();

        // Accept clients until cancelled or the LS dies.
        var accept = AcceptLoopAsync(pipeName, mux, Log, cts.Token);
        var serverExit = WaitForExitAsync(server, cts.Token);
        await Task.WhenAny(accept, serverExit).ConfigureAwait(false);

        cts.Cancel();
        foreach (var le in loadedExtensions) { try { await le.Extension.DisposeAsync().ConfigureAwait(false); } catch { } }
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
