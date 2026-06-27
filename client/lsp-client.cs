// claude-roslyn-lsp — thin LSP client (compiled: `dotnet exec ClaudeRoslynLsp.Client.dll`).
//
// What Claude Code's LSP host spawns, once per instance. It is deliberately tiny: it finds (or starts) the ONE shared
// Roslyn daemon for this workspace (DaemonConnector) and then relays LSP messages between Claude's stdio and the daemon
// over a Sluice shared-memory frame channel. All the heavy Roslyn work — and its memory — lives in the single daemon,
// shared across every Claude instance on this workspace (N instances cost 1× the workspace, not N×).
//
//   The client↔daemon hop is a Sluice ShmFrameChannel (erinloy/Sluice): zero-serialization, zero-copy shared memory,
//   frame-native (IFrameChannel was designed for LSP's Content-Length messages). Each frame is ONE LSP message body —
//   the frame boundary replaces Content-Length on the wire. The client owns Content-Length framing only on its stdio
//   seam with Claude (LspMessageReader/Writer).
//
//   stdout is the LSP JSON-RPC wire — NOTHING but the daemon's message bodies (re-framed) may go there. Logs → stderr.

using System.Runtime.CompilerServices;
using System.Text;
using ClaudeRoslynLsp.Bridge;   // PipeKey, DaemonConnector, LspMessageReader/Writer (linked) — single source of truth
using ClaudeRoslynLsp.Client;   // DaemonRouter — per-file routing across repos + daemon-death watch
using Sluice;                   // IFrameChannel — the client↔daemon transport

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

IFrameChannel? channel = await DaemonConnector.ConnectAsync(endpoint, root, solution, pluginRoot, Log);
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

// The router owns the down-pump(s) and routes each up-message to the daemon that owns its file. With a single repo it is
// exactly the old behaviour (everything → the primary daemon) PLUS the daemon-death watch; with files outside the home
// root it spins up a per-repo secondary daemon so cross-repo work gets full project-aware analysis. Set
// CLAUDE_ROSLYN_MULTI_REPO=0 to keep everything on the primary (kill-switch) while still detecting daemon death.
bool multiRepo = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_MULTI_REPO") != "0";
// On home-daemon death we must terminate even though the up-loop is parked on a blocking stdin read (which doesn't observe
// the token). A hard exit makes Claude Code's LSP host see the server die and restart it — the clean recovery from a
// daemon restart. Without this the client would block forever on the dead ring, hanging Claude's in-flight LSP call.
void OnPrimaryDeath()
{
    Log("primary daemon died — exiting so Claude Code restarts the LSP and reconnects to the live daemon");
    try { cts.Cancel(); } catch { }
    Environment.Exit(17);
}
using var router = new DaemonRouter(channel, root, pluginRoot, writer, Log, cts.Token, OnPrimaryDeath, multiRepo);
Log($"multi-repo routing: {(multiRepo ? "on" : "off (kill-switch)")}");

// up: Claude stdin → router. On stdin EOF (Claude closed the server) we break and exit; the daemon's PID-watch reaps us.
try
{
    while (!cts.IsCancellationRequested)
    {
        LspMessage? msg = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
        if (msg is null) break; // Claude closed stdin
        router.Up(msg);
    }
}
catch { /* stdin closed / cancelled */ }
finally { cts.Cancel(); }

try { channel.Dispose(); } catch { }
return 0;
