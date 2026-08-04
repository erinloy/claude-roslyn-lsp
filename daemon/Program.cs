using ClaudeRoslynLsp.Bridge;   // RoslynAcquirer, RoslynServer, PipeKey (linked)
using ClaudeRoslynLsp.Daemon;
using ConsoleAppFramework;
using Sluice;                   // ShmFrameListener — the client rendezvous (NamedPipeServerStream analogue)

// The shared Roslyn daemon — one per workspace, owns ONE Roslyn language server + workspace, multiplexed onto many thin
// clients over a Sluice shared-memory channel. Launched (detached) by lsp-client.cs when no daemon for the workspace is yet running.
await ConsoleApp.RunAsync(args, RunDaemon);

/// <summary>Run the shared daemon.</summary>
/// <param name="root">Workspace root (the LS rootUri; also drives solution discovery). Defaults to the cwd.</param>
/// <param name="pipe">Channel key to serve (the Sluice rendezvous name). Defaults to the key derived from the root (clients match it).</param>
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

/// <summary>Exits the daemon after a continuous window with zero clients, so an idle workspace frees its memory —
/// OR after the shared Roslyn server exceeds a memory ceiling, which is the multi-agent case the zero-client rule
/// cannot reach.
///
/// <para>🔴 WHY THE SECOND TRIGGER EXISTS. The zero-client rule is this class's ONLY memory bound and it is
/// DISABLED BY CONVERGENCE: with N agents fanned onto one server, <c>ClientCount</c> never reaches zero, so the
/// shutdown never fires and the process accumulates for days. It works perfectly for ONE agent — which is why this
/// only manifests in a fleet, and why every manual restart appeared to "fix" it (a restart is the bound that the
/// idle rule was supposed to provide automatically).</para>
///
/// <para>MEASURED 2026-08-03, pid 58324, 313m uptime: working set 17.04 GB, of which <b>16.2 GB is LIVE gen-2
/// managed heap</b> (loh 0.5 GB, committed 18.2 GB), climbing ~1.5 GB/h with no plateau. Live gen-2 means the
/// objects are REACHABLE, so this is not fragmentation and not the GC withholding free segments — <b>no GC setting
/// or <c>System.GC.Server</c> flip can touch it</b>, and attempting one is misleading because it requires a restart
/// whose memory drop then gets credited to the setting. Retained content is Roslyn semantic state including
/// incremental source-generator caches (<c>IStateTable</c>, <c>SourceGeneratorSyntaxTreeInfo</c>,
/// <c>TableEntry&lt;(SemanticModel, TypeDeclarationSyntax)&gt;</c>), which are keyed BY SYNTAX TREE and therefore
/// grow with the union of open documents across every connected agent, per project, across a 345-project
/// workspace.</para>
///
/// <para>⚠️ THIS IS A BOUND, NOT THE CURE. The cure is idle-document eviction in <see cref="LspMultiplexer"/>: it
/// ref-counts didOpen/didClose and forwards a close only when the LAST client drops, while LSP clients do not
/// reliably send didClose for a file they merely read — so the shared server holds the monotonic UNION of every
/// document any agent ever opened. Eviction is the real fix and it is a document-lifecycle change (an evicted doc
/// must be re-opened from disk before a later didChange, or the server sees a change for a closed document).
/// This ceiling exists so the fleet is not carrying 17 GB while that is written and verified.</para>
///
/// <para>Tune with <c>CLAUDE_ROSLYN_MEMORY_CEILING_GB</c>; <c>0</c> disables the trigger entirely (restoring the
/// previous zero-client-only behaviour). Recycling is safe by the same machinery the idle path already relies on:
/// the daemon announces its PID so clients watch for its death, and the next request spawns a fresh daemon.</para>
/// </summary>
sealed class IdleShutdown
{
    private readonly LspMultiplexer _mux;
    private readonly TimeSpan _timeout;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts;
    private DateTime _zeroSince = DateTime.UtcNow; // no clients have connected yet
    private DateTime _overCeilingSince = DateTime.MaxValue; // MaxValue ⇒ not currently over the ceiling

    /// <summary>Roslyn working-set ceiling in bytes; 0 disables. Read once at construction so a running daemon has a
    /// fixed policy. Default 12 GB: high enough that a 345-project workspace loads and serves normally (the measured
    /// steady state is well under it), low enough to bound the fleet well below the 17 GB observed.</summary>
    private static readonly long MemoryCeilingBytes = ReadCeilingBytes();

    private static long ReadCeilingBytes()
    {
        var raw = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_MEMORY_CEILING_GB");
        // Invalid input falls back to the DEFAULT rather than to 0/disabled: a typo must not silently remove the
        // only bound this class has in a multi-agent fleet.
        if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var gb))
            return gb <= 0 ? 0 : (long)(gb * 1024d * 1024d * 1024d);
        return 12L * 1024 * 1024 * 1024;
    }

    /// <summary>Largest working set among live Roslyn server processes, or 0 if none/unreadable. By process NAME
    /// rather than a held handle so this cannot itself pin a process, and so it still reports correctly if the
    /// server was respawned underneath us. Unreadable ⇒ 0 ⇒ never trips: a measurement failure must not recycle a
    /// healthy daemon.</summary>
    private static long RoslynWorkingSetBytes()
    {
        long max = 0;
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("Microsoft.CodeAnalysis.LanguageServer"))
                using (p) { if (p.WorkingSet64 > max) max = p.WorkingSet64; }
        }
        catch { /* a diagnostic read must never take down the daemon */ }
        return max;
    }

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

                // The multi-agent bound. SUSTAINED, not instantaneous: two consecutive polls (~30s apart) must both
                // exceed the ceiling, so a transient allocation spike during a solution load or a build-driven
                // re-analysis cannot recycle a healthy daemon. A single sample would make this trigger-happy at
                // exactly the moments the server is doing its most legitimate work.
                if (MemoryCeilingBytes > 0)
                {
                    long ws = RoslynWorkingSetBytes();
                    if (ws > MemoryCeilingBytes)
                    {
                        if (_overCeilingSince == DateTime.MaxValue)
                        {
                            _overCeilingSince = DateTime.UtcNow;
                            _log($"Roslyn working set {ws / 1024d / 1024d / 1024d:0.00} GB is over the " +
                                 $"{MemoryCeilingBytes / 1024d / 1024d / 1024d:0.0} GB ceiling — confirming before recycling");
                        }
                        else
                        {
                            _log($"Roslyn working set {ws / 1024d / 1024d / 1024d:0.00} GB over the " +
                                 $"{MemoryCeilingBytes / 1024d / 1024d / 1024d:0.0} GB ceiling for two consecutive checks " +
                                 $"({_mux.ClientCount} client(s) connected) — recycling. This is a BOUND, not a cure: the " +
                                 $"retained state is the union of open documents across clients (see IdleShutdown remarks). " +
                                 $"Tune or disable with CLAUDE_ROSLYN_MEMORY_CEILING_GB.");
                            _cts.Cancel();
                            return;
                        }
                    }
                    else _overCeilingSince = DateTime.MaxValue; // dropped back under — require a fresh sustained breach
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
