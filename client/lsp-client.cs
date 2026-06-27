// claude-roslyn-lsp — thin LSP client (compiled: `dotnet exec ClaudeRoslynLsp.Client.dll`).
//
// What Claude Code's LSP host spawns, once per instance. It is deliberately tiny: it finds (or starts) the ONE shared
// Roslyn daemon for this workspace and then relays LSP messages between Claude's stdio and the daemon over a Sluice
// shared-memory frame channel. All the heavy Roslyn work — and its memory — lives in the single daemon, shared across
// every Claude instance on this workspace (N instances cost 1× the workspace, not N×).
//
//   The client↔daemon hop is a Sluice ShmFrameChannel (erinloy/Sluice): zero-serialization, zero-copy shared memory,
//   frame-native (IFrameChannel was designed for LSP's Content-Length messages). Each frame is ONE LSP message body —
//   the frame boundary replaces Content-Length on the wire. The client owns the Content-Length framing only on its
//   stdio seam with Claude (LspMessageReader/Writer).
//
//   stdout is the LSP JSON-RPC wire — NOTHING but the daemon's message bodies (re-framed) may go there. Logs → stderr.
//
// Launch is `dotnet exec <prebuilt dll>`, NEVER `dotnet run`: `dotnet run` rebuilds on every launch, and N instances
// launching the same shared cache copy concurrently race to build+lock the same output DLL → MSB3027 "build failed".
// The dll is pre-built by boot/ensure-built.ps1 (SessionStart hook); we only ever execute it.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ClaudeRoslynLsp.Bridge;   // PipeKey + LspMessageReader/Writer (linked) — single source of truth
using Sluice;                   // ShmFrameChannel / IFrameChannel — the client↔daemon transport

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
string endpoint = PipeKey.ForRoot(root);
string? solution = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_SOLUTION");

// pluginRoot is where the prebuilt daemon DLL lives — prefer the env Claude sets, else derive from this assembly.
string pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT")
    ?? Directory.GetParent(Path.GetDirectoryName(ScriptPath())!)!.FullName;

Log($"workspace root: {root}");
Log($"endpoint: {endpoint}");

IFrameChannel? channel = await ConnectOrStartDaemonAsync(endpoint, root, solution, pluginRoot, Log);
if (channel is null) { Log("FATAL: could not reach the roslyn daemon"); return 1; }

Log("connected to daemon — proxying");

// Announce our PID first: shared memory gives no peer-death signal, so the daemon reaps this session by watching the
// process. Frame is a tiny JSON object the daemon intercepts (never forwarded to the language server).
try { channel.WriteFrame(Encoding.UTF8.GetBytes($"{{\"{PipeKey.PidHelloKey}\":{Environment.ProcessId}}}")); }
catch (Exception ex) { Log($"hello failed: {ex.Message}"); }

using var cts = new CancellationTokenSource();
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var reader = new LspMessageReader(stdin);
var writer = new LspMessageWriter(stdout);

// down: daemon frames → Claude stdout. ShmFrameChannel reads are blocking/synchronous, so this owns a dedicated thread.
// It is the SOLE consumer of the inbound ring and the SOLE writer of stdout.
var down = new Thread(() =>
{
    try
    {
        while (!cts.IsCancellationRequested && channel.WaitForFrame(cts.Token))
        {
            while (channel.TryReadFrame(out var span))
            {
                byte[] frame = span.ToArray();   // copy out before AdvanceFrame frees the slot
                channel.AdvanceFrame();
                writer.WriteRawAsync(frame, cts.Token).GetAwaiter().GetResult(); // re-frame with Content-Length
            }
        }
    }
    catch { /* channel closed / cancelled */ }
    finally { cts.Cancel(); }
}) { IsBackground = true, Name = "crlsp-down" };
down.Start();

// up: Claude stdin → daemon frames. The SOLE producer of the outbound ring. On stdin EOF (Claude closed the server)
// we break and exit the process — the daemon's PID-watch then reaps our session.
try
{
    while (!cts.IsCancellationRequested)
    {
        LspMessage? msg = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
        if (msg is null) break; // Claude closed stdin
        channel.WriteFrame(msg.Raw, cts.Token);
    }
}
catch { /* stdin closed / cancelled */ }
finally { cts.Cancel(); }

try { channel.Dispose(); } catch { }
return 0;

static async Task<IFrameChannel?> ConnectOrStartDaemonAsync(
    string endpoint, string root, string? solution, string pluginRoot, Action<string> log)
{
    // 1. Fast path: a daemon is already alive → connect to it.
    if (DaemonAlive(endpoint) && TryConnect(endpoint) is { } fast) return fast;

    // 2. Elect a single spawner so concurrent clients don't start duplicate daemons.
    using var spawnLock = new Mutex(initiallyOwned: false, PipeKey.MutexFor(endpoint));
    bool owner = false;
    try { owner = spawnLock.WaitOne(0); } catch (AbandonedMutexException) { owner = true; } catch { }

    try
    {
        if (owner && !DaemonAlive(endpoint))
        {
            log("no daemon — starting one (detached)");
            StartDaemon(pluginRoot, root, solution, log);
        }
        // 3. Wait for the daemon to come up (first start also loads the workspace), then connect.
        for (int i = 0; i < 240; i++) // up to ~120s
        {
            if (DaemonAlive(endpoint) && TryConnect(endpoint) is { } p) return p;
            await Task.Delay(500);
        }
        return null;
    }
    finally { if (owner) { try { spawnLock.ReleaseMutex(); } catch { } } }
}

// Liveness via the daemon's lifetime mutex: if WE can acquire it, no daemon holds it (not alive); release at once.
static bool DaemonAlive(string endpoint)
{
    using var m = new Mutex(initiallyOwned: false, PipeKey.DaemonAliveMutex(endpoint));
    bool got = false;
    try { got = m.WaitOne(0); } catch (AbandonedMutexException) { got = true; } catch { return false; }
    if (got) { try { m.ReleaseMutex(); } catch { } return false; }
    return true;
}

// Only called when a daemon is alive: opens the accept ring + rendezvous. Returns null if the listener isn't up yet.
static IFrameChannel? TryConnect(string endpoint)
{
    try { return ShmFrameChannel.Connect(endpoint, PipeKey.FrameCapacity); }
    catch { return null; }
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
