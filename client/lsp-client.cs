// claude-roslyn-lsp — thin LSP client (file-based .NET 10 app: `dotnet run --file lsp-client.cs`).
//
// What Claude Code's LSP host spawns, once per instance. It is deliberately tiny: it finds (or starts) the ONE shared
// Roslyn daemon for this workspace and then just pumps bytes between Claude's stdio and the daemon's named pipe. All the
// heavy Roslyn work — and its memory — lives in the single daemon, shared across every Claude instance on this workspace.
//
//   stdout is the LSP JSON-RPC wire — NOTHING but the daemon's bytes may go there. Diagnostics go to stderr only.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

static string ScriptPath([CallerFilePath] string p = "") => p;
void Log(string m) => Console.Error.WriteLine($"[roslyn-client] {m}");

string root = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT")
    ?? Directory.GetCurrentDirectory();
string pipeName = PipeForRoot(root);
string? solution = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_SOLUTION");

// pluginRoot/daemon/…csproj — prefer the env Claude sets, else derive from this script's own location.
string pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT")
    ?? Directory.GetParent(Path.GetDirectoryName(ScriptPath())!)!.FullName;
string daemonCsproj = Path.Combine(pluginRoot, "daemon", "ClaudeRoslynLsp.Daemon.csproj");

Log($"workspace root: {root}");
Log($"pipe: {pipeName}");

NamedPipeClientStream? pipe = await ConnectOrStartDaemonAsync(pipeName, root, solution, daemonCsproj, Log);
if (pipe is null) { Log("FATAL: could not reach the roslyn daemon"); return 1; }

Log("connected to daemon — proxying");
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();
using var cts = new CancellationTokenSource();

// Pump both directions; when either side closes, tear down.
Task up = Copy(stdin, pipe, cts);     // Claude → daemon
Task down = Copy(pipe, stdout, cts);  // daemon → Claude
await Task.WhenAny(up, down);
cts.Cancel();
try { pipe.Dispose(); } catch { }
return 0;

static async Task Copy(Stream from, Stream to, CancellationTokenSource cts)
{
    var buf = new byte[16 * 1024];
    try
    {
        int n;
        while ((n = await from.ReadAsync(buf, cts.Token)) > 0)
        {
            await to.WriteAsync(buf.AsMemory(0, n), cts.Token);
            await to.FlushAsync(cts.Token);
        }
    }
    catch { }
    finally { try { cts.Cancel(); } catch { } }
}

static async Task<NamedPipeClientStream?> ConnectOrStartDaemonAsync(
    string pipeName, string root, string? solution, string daemonCsproj, Action<string> log)
{
    // 1. Fast path: a daemon is already running.
    var pipe = await TryConnectAsync(pipeName, 750);
    if (pipe is not null) return pipe;

    // 2. Elect a single spawner so concurrent clients don't start duplicate daemons.
    using var spawnLock = new Mutex(initiallyOwned: false, $"{pipeName}-spawn");
    bool owner = false;
    try { owner = spawnLock.WaitOne(0); } catch { }

    try
    {
        if (owner && await TryConnectAsync(pipeName, 250) is null)
        {
            log("no daemon — starting one (detached)");
            StartDaemon(daemonCsproj, root, solution, log);
        }
        // 3. Wait for the daemon to come up (first start also builds + loads the workspace).
        for (int i = 0; i < 240; i++) // up to ~120s
        {
            var p = await TryConnectAsync(pipeName, 500);
            if (p is not null) return p;
            await Task.Delay(500);
        }
        return null;
    }
    finally { if (owner) { try { spawnLock.ReleaseMutex(); } catch { } } }
}

static async Task<NamedPipeClientStream?> TryConnectAsync(string pipeName, int timeoutMs)
{
    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    try { await pipe.ConnectAsync(timeoutMs); return pipe; }
    catch { pipe.Dispose(); return null; }
}

static void StartDaemon(string daemonCsproj, string root, string? solution, Action<string> log)
{
    // `dotnet run --project` builds-then-runs (cached after first build). UseShellExecute detaches the daemon fully so
    // it OUTLIVES this client and gets NO inherited console — its stdout can't leak onto our LSP wire. It logs to its
    // own file (daemon-<pipe>.log under the plugin data dir).
    var args = new List<string> { "run", "--project", daemonCsproj, "-c", "Release", "--",
                                   "--root", root };
    if (!string.IsNullOrWhiteSpace(solution)) { args.Add("--solution"); args.Add(solution!); }

    var psi = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = true,        // detach: child survives us, no inherited stdio
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = root,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    try { Process.Start(psi); }
    catch (Exception ex) { log($"failed to start daemon: {ex.Message}"); }
}

// MUST match ClaudeRoslynLsp.Bridge.PipeKey.ForRoot exactly (the daemon links that file; this one can't).
static string PipeForRoot(string root)
{
    string norm = root.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
    string hex = Convert.ToHexString(hash).ToLowerInvariant()[..16];
    return $"roslyn-lsp-{hex}";
}
