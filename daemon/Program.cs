using System.IO.Pipes;
using ClaudeRoslynLsp.Bridge;   // RoslynAcquirer, RoslynServer, PipeKey (linked)
using ClaudeRoslynLsp.Daemon;

// The shared Roslyn daemon — one per workspace, owns ONE Roslyn language server + workspace, multiplexed onto many thin
// clients over a named pipe. Launched (detached) by lsp-client.cs when no daemon for the workspace is yet running.
//
//   --root <path>        workspace root (the LS rootUri; also drives solution discovery)
//   --pipe <name>        the named pipe to serve (clients derive the same name from the root)
//   --solution <path>    optional explicit solution/project override (else CLAUDE_ROSLYN_SOLUTION, else discovery)
//   --idle-seconds <n>   shut down after this long with zero clients (default 600)

string root = ArgValue(args, "--root") ?? Directory.GetCurrentDirectory();
string pipeName = ArgValue(args, "--pipe") ?? PipeKey.ForRoot(root);
string? solutionOverride = ArgValue(args, "--solution") ?? Environment.GetEnvironmentVariable(SolutionLocator.OverrideEnvVar);
int idleSeconds = int.TryParse(ArgValue(args, "--idle-seconds"), out int s) ? s : 600;

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
if (!single.WaitOne(0))
{
    Log($"another daemon already owns {pipeName}; exiting");
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cts.Cancel(); } catch { } };

try
{
    Log($"daemon starting — root={root} pipe={pipeName} idle={idleSeconds}s");
    string logDir = Path.Combine(dataDir, "logs");
    string serverDll = await RoslynAcquirer.EnsureServerAsync(dataDir, Log, cts.Token).ConfigureAwait(false);

    using var server = RoslynServer.Start(serverDll, logDir, Log);
    Log($"roslyn server pid {server.Id} started");

    var mux = new LspMultiplexer(server.StandardInput.BaseStream, server.StandardOutput.BaseStream, Log, cts.Token);
    await mux.StartAsync(root, solutionOverride).ConfigureAwait(false);

    var idle = new IdleShutdown(mux, TimeSpan.FromSeconds(idleSeconds), Log, cts);
    idle.Start();

    // Accept clients until cancelled or the LS dies.
    var accept = AcceptLoopAsync(pipeName, mux, Log, cts.Token);
    var serverExit = WaitForExitAsync(server, cts.Token);
    await Task.WhenAny(accept, serverExit).ConfigureAwait(false);

    cts.Cancel();
    try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch { }
    Log("daemon exiting");
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception ex) { Log($"FATAL: {ex}"); return 1; }

static async Task AcceptLoopAsync(string pipeName, LspMultiplexer mux, Action<string> log, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            mux.AddClient(pipe);
        }
        catch (OperationCanceledException) { pipe.Dispose(); break; }
        catch (Exception ex) { log($"accept error: {ex.Message}"); pipe.Dispose(); }
    }
}

static async Task WaitForExitAsync(System.Diagnostics.Process p, CancellationToken ct)
{
    try { await p.WaitForExitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
}

static string? ArgValue(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
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
