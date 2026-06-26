// claude-roslyn-lsp — thin LSP client (compiled: `dotnet exec ClaudeRoslynLsp.Client.dll`).
//
// What Claude Code's LSP host spawns, once per instance. It is deliberately tiny: it finds (or starts) the ONE shared
// Roslyn daemon for this workspace and then just pumps bytes between Claude's stdio and the daemon's named pipe. All the
// heavy Roslyn work — and its memory — lives in the single daemon, shared across every Claude instance on this workspace.
//
//   stdout is the LSP JSON-RPC wire — NOTHING but the daemon's bytes may go there. Diagnostics go to stderr only.
//
// Launch is `dotnet exec <prebuilt dll>`, NEVER `dotnet run`: `dotnet run` rebuilds on every launch, and N instances
// launching the same shared cache copy concurrently race to build+lock the same output DLL → MSB3027 "build failed".
// The dll is pre-built by boot/ensure-built.ps1 (SessionStart hook); we only ever execute it.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using ClaudeRoslynLsp.Bridge;   // PipeKey (linked) — single source of truth for the pipe name

static string ScriptPath([CallerFilePath] string p = "") => p;

// Unconditional launch record — written before ANYTHING else can fail — so we can tell "Claude never launched the
// client" (no file) from "Claude launched it but it died" (file present, with the failure in client.log).
string launchLogDir = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA") is { Length: > 0 } pd
    ? Path.Combine(pd, "roslyn")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-roslyn-lsp");
try
{
    Directory.CreateDirectory(launchLogDir);
    File.AppendAllText(Path.Combine(launchLogDir, "client-launch.log"),
        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] launched pid={Environment.ProcessId} cwd={Directory.GetCurrentDirectory()} args=[{string.Join(' ', args)}]{Environment.NewLine}");
}
catch { /* best-effort */ }

void Log(string m)
{
    Console.Error.WriteLine($"[roslyn-client] {m}");
    try { File.AppendAllText(Path.Combine(launchLogDir, "client.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {m}{Environment.NewLine}"); } catch { }
}

string root = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT")
    ?? Directory.GetCurrentDirectory();
string pipeName = PipeKey.ForRoot(root);
string? solution = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_SOLUTION");

// pluginRoot is where the prebuilt daemon DLL lives — prefer the env Claude sets, else derive from this assembly.
string pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT")
    ?? Directory.GetParent(Path.GetDirectoryName(ScriptPath())!)!.FullName;

Log($"workspace root: {root}");
Log($"pipe: {pipeName}");

NamedPipeClientStream? pipe = await ConnectOrStartDaemonAsync(pipeName, root, solution, pluginRoot, Log);
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
    string pipeName, string root, string? solution, string pluginRoot, Action<string> log)
{
    // 1. Fast path: a daemon is already running.
    var pipe = await TryConnectAsync(pipeName, 750);
    if (pipe is not null) return pipe;

    // 2. Elect a single spawner so concurrent clients don't start duplicate daemons.
    using var spawnLock = new Mutex(initiallyOwned: false, PipeKey.MutexFor(pipeName));
    bool owner = false;
    try { owner = spawnLock.WaitOne(0); } catch { }

    try
    {
        if (owner && await TryConnectAsync(pipeName, 250) is null)
        {
            log("no daemon — starting one (detached)");
            StartDaemon(pluginRoot, root, solution, log);
        }
        // 3. Wait for the daemon to come up (first start also loads the workspace).
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

static void StartDaemon(string pluginRoot, string root, string? solution, Action<string> log)
{
    string daemonDll = Path.Combine(pluginRoot, "daemon", "bin", "Release", "net8.0", "ClaudeRoslynLsp.Daemon.dll");

    // The SessionStart hook (boot/ensure-built.ps1) normally pre-builds this. If it hasn't yet (manual run, or the LSP
    // host started us before the hook finished on a cold cache), build it ONCE — serialized by a machine-wide mutex so
    // concurrent clients across workspaces never race the same output. We `exec` the DLL; we never `dotnet run` it.
    if (!File.Exists(daemonDll))
        EnsureBuilt(Path.Combine(pluginRoot, "daemon", "ClaudeRoslynLsp.Daemon.csproj"), daemonDll, "daemon", log);
    if (!File.Exists(daemonDll)) { log($"daemon dll missing after build attempt: {daemonDll}"); return; }

    // `dotnet exec` runs the built assembly with no build step. UseShellExecute detaches the daemon fully so it OUTLIVES
    // this client and gets NO inherited console — its stdout can't leak onto our LSP wire. It logs to its own file.
    var psi = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = true,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = root,
    };
    psi.ArgumentList.Add("exec");
    psi.ArgumentList.Add(daemonDll);
    psi.ArgumentList.Add("--root"); psi.ArgumentList.Add(root);
    if (!string.IsNullOrWhiteSpace(solution)) { psi.ArgumentList.Add("--solution"); psi.ArgumentList.Add(solution!); }
    try { Process.Start(psi); }
    catch (Exception ex) { log($"failed to start daemon: {ex.Message}"); }
}

// Build a project to its Release DLL exactly once across all instances. The mutex collapses N concurrent first-launches
// into ONE build (the rest wait, then find the DLL already present) — the cure for the `dotnet run` build-lock race.
static void EnsureBuilt(string csproj, string dll, string label, Action<string> log)
{
    using var buildLock = new Mutex(initiallyOwned: false, $"roslyn-lsp-build-{label}");
    bool held = false;
    try { held = buildLock.WaitOne(TimeSpan.FromMinutes(3)); } catch (AbandonedMutexException) { held = true; } catch { }
    try
    {
        if (File.Exists(dll)) return; // a sibling built it while we waited
        log($"building {label} (first run) …");
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("build"); psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Release");
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) log($"{label} build failed (exit {p.ExitCode}): {err}");
    }
    catch (Exception ex) { log($"{label} build error: {ex.Message}"); }
    finally { if (held) { try { buildLock.ReleaseMutex(); } catch { } } }
}
