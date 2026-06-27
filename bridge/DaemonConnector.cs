using System.Diagnostics;
using Sluice;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// Finds (or starts) the ONE shared Roslyn daemon for a workspace and returns a connected Sluice frame channel. Shared
/// by every thin process that talks to the daemon — the LSP relay client and the refactor MCP — so the connect /
/// liveness / spawn-election / first-run-build logic lives in exactly one place (the reusable broker's connect half).
/// </summary>
public static class DaemonConnector
{
    /// <summary>Connect to the workspace's daemon, starting it (once, election-guarded) if none is alive yet.</summary>
    public static async Task<IFrameChannel?> ConnectAsync(
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
                await Task.Delay(500).ConfigureAwait(false);
            }
            return null;
        }
        finally { if (owner) { try { spawnLock.ReleaseMutex(); } catch { } } }
    }

    /// <summary>Liveness via the daemon's lifetime mutex: if WE can acquire it, none is running; release at once.</summary>
    public static bool DaemonAlive(string endpoint)
    {
        using var m = new Mutex(initiallyOwned: false, PipeKey.DaemonAliveMutex(endpoint));
        bool got = false;
        try { got = m.WaitOne(0); } catch (AbandonedMutexException) { got = true; } catch { return false; }
        if (got) { try { m.ReleaseMutex(); } catch { } return false; }
        return true;
    }

    // Only called when a daemon is alive: opens the accept ring + rendezvous. Null if the listener isn't up yet.
    private static IFrameChannel? TryConnect(string endpoint)
    {
        try { return ShmFrameChannel.Connect(endpoint, PipeKey.FrameCapacity); }
        catch { return null; }
    }

    private static void StartDaemon(string pluginRoot, string root, string? solution, Action<string> log)
    {
        string daemonDll = Path.Combine(pluginRoot, "daemon", "bin", "Release", "net8.0", "ClaudeRoslynLsp.Daemon.dll");

        // The SessionStart hook (boot/ensure-built.ps1) normally pre-builds this. If it hasn't yet (manual run, or the
        // host started us before the hook finished on a cold cache), build it ONCE — serialized by a machine-wide mutex
        // so concurrent clients across workspaces never race the same output. We `exec` the DLL; never `dotnet run` it.
        if (!File.Exists(daemonDll))
            EnsureBuilt(Path.Combine(pluginRoot, "daemon", "ClaudeRoslynLsp.Daemon.csproj"), daemonDll, "daemon", log);
        if (!File.Exists(daemonDll)) { log($"daemon dll missing after build attempt: {daemonDll}"); return; }

        // `dotnet exec` runs the built assembly with no build step. UseShellExecute detaches the daemon fully so it
        // OUTLIVES the caller and gets NO inherited console — its stdout can't leak onto any LSP wire.
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

    // Build a project to its Release DLL exactly once across all instances. The mutex collapses N concurrent
    // first-launches into ONE build (the rest wait, then find the DLL present) — the cure for the build-lock race.
    private static void EnsureBuilt(string csproj, string dll, string label, Action<string> log)
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
}
