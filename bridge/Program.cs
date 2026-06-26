using ClaudeRoslynLsp.Bridge;

// claude-roslyn-lsp bridge entry point.
//
//   --stdio      (default) run as an LSP server over stdin/stdout: acquire the Roslyn server, spawn it, proxy traffic,
//                drive solution/open. The ONLY thing written to stdout is LSP JSON-RPC; ALL diagnostics go to stderr.
//   --download   acquire the Roslyn server (and exit) — for pre-provisioning without starting a session.
//   --version    print the bridge version and exit.

bool download = args.Contains("--download");
if (args.Contains("--version") || args.Contains("-v"))
{
    Console.WriteLine("claude-roslyn-lsp bridge 0.1.0");
    return 0;
}

// Logs NEVER go to stdout (that is the LSP wire). stderr + an optional rolling file under the data dir.
string dataDir = ResolveDataDir();
Directory.CreateDirectory(dataDir);
string logFile = Path.Combine(dataDir, "bridge.log");
object logGate = new();
void Log(string msg)
{
    string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
    lock (logGate)
    {
        Console.Error.WriteLine(line);
        try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { /* best-effort */ }
    }
}

using var cts = new CancellationTokenSource();
void RequestCancel() { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestCancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestCancel();

try
{
    string logDir = Path.Combine(dataDir, "logs");
    string serverDll = await RoslynAcquirer.EnsureServerAsync(dataDir, Log, cts.Token).ConfigureAwait(false);

    if (download)
    {
        Log("server acquired; --download requested, exiting");
        return 0;
    }

    string? solutionOverride = Environment.GetEnvironmentVariable(SolutionLocator.OverrideEnvVar);
    var proxy = new LspStdioProxy(serverDll, logDir, solutionOverride, Log);
    int code = await proxy.RunAsync(cts.Token).ConfigureAwait(false);
    Log($"bridge exiting with code {code}");
    return code;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Log($"FATAL: {ex}");
    return 1;
}

// The plugin passes CLAUDE_PLUGIN_DATA (a per-plugin writable dir). Fall back to a stable per-user location so the bridge
// also works when run by hand outside Claude Code.
static string ResolveDataDir()
{
    string? fromPlugin = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA");
    if (!string.IsNullOrWhiteSpace(fromPlugin)) return Path.Combine(fromPlugin, "roslyn");
    string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(baseDir, "claude-roslyn-lsp");
}
