using System.Text.Json.Nodes;
using ClaudeRoslynLsp.Bridge;

// claude-roslyn-lsp bridge entry point.
//
//   --stdio        (default) run as an LSP server over stdin/stdout: acquire the Roslyn server, spawn it, proxy traffic,
//                  drive solution/open. The ONLY thing written to stdout is LSP JSON-RPC; ALL diagnostics go to stderr.
//   --download     acquire the Roslyn server (and exit) — for pre-provisioning without starting a session.
//   --capabilities interrogate the server for the capabilities/extensions it exposes at runtime (code-action kinds,
//                  executable commands, providers, semantic-token legend) and print them. The summary goes to stderr;
//                  the raw capabilities JSON is the LAST block on stdout. Optional arg: a workspace root path.
//   --version      print the bridge version and exit.

bool download = args.Contains("--download");
bool capabilities = args.Contains("--capabilities");
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

    if (capabilities)
    {
        // A workspace root isn't required to read capabilities, but pass one through if given (first non-flag arg, else cwd).
        string rootPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory();
        var probe = new CapabilitiesProbe(serverDll, logDir, rootPath, Log);
        JsonObject? caps = await probe.ProbeAsync(cts.Token).ConfigureAwait(false);
        string report = CapabilitiesProbe.Summarize(caps);
        Console.Error.WriteLine(report);   // human summary → stderr
        Console.Out.WriteLine(report);     // full report (incl. raw JSON) → stdout for machine capture
        return caps is null ? 1 : 0;
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
