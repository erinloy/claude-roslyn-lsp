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
    /// <param name="ct">Cancels the spin-up wait. Without it a caller's own deadline could not reach this loop, so a
    /// daemon that never comes up would hold the caller for the full ~120s regardless of its budget.</param>
    public static async Task<IFrameChannel?> ConnectAsync(
        string endpoint, string root, string? solution, string pluginRoot, Action<string> log,
        CancellationToken ct = default, int requestIdleMin = 0)
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
                StartDaemon(pluginRoot, root, solution, log, requestIdleMin);
            }
            // 3. Wait for the daemon to come up (first start also loads the workspace), then connect. Bounded twice: by
            //    the loop (~120s) and by the caller's token, so a caller with a tighter deadline is never held past it.
            for (int i = 0; i < 240 && !ct.IsCancellationRequested; i++) // up to ~120s
            {
                if (DaemonAlive(endpoint) && TryConnect(endpoint) is { } p) return p;
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
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

    private static void StartDaemon(string pluginRoot, string root, string? solution, Action<string> log, int requestIdleMin)
    {
        string daemonDll = Path.Combine(pluginRoot, "daemon", "bin", "Release", "net8.0", "ClaudeRoslynLsp.Daemon.dll");

        // The SessionStart hook (boot/ensure-built.ps1) normally pre-builds this. If it hasn't yet (manual run, or the
        // host started us before the hook finished on a cold cache), build it ONCE — serialized by a machine-wide mutex
        // so concurrent clients across workspaces never race the same output. We `exec` the DLL; never `dotnet run` it.
        if (!File.Exists(daemonDll))
            EnsureBuilt(Path.Combine(pluginRoot, "daemon", "ClaudeRoslynLsp.Daemon.csproj"), daemonDll, "daemon", log);
        if (!File.Exists(daemonDll)) { log($"daemon dll missing after build attempt: {daemonDll}"); return; }

        // Launch through boot/run.ps1, which shadow-copies the build output and execs from the copy — so the daemon NEVER
        // locks daemon/bin (rebuilds stay possible at any number of concurrent instances). `--detached` makes run.ps1
        // Start-Process the daemon hidden+detached (it outlives this caller, gets no inherited console) and return at once.
        string runPs1 = Path.Combine(pluginRoot, "boot", "run.ps1");
        var psi = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root,
        };
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(runPs1);
        psi.ArgumentList.Add("daemon"); psi.ArgumentList.Add("--detached");
        psi.ArgumentList.Add("--root"); psi.ArgumentList.Add(root);
        if (!string.IsNullOrWhiteSpace(solution)) { psi.ArgumentList.Add("--solution"); psi.ArgumentList.Add(solution!); }
        if (requestIdleMin > 0) { psi.ArgumentList.Add("--request-idle-min"); psi.ArgumentList.Add(requestIdleMin.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        try { Process.Start(psi); }
        catch (Exception ex) { log($"failed to start daemon: {ex.Message}"); }
    }

    // A first-run build must not be able to park the caller forever: a `dotnet build` that wedges (NuGet restore hung on a
    // dead feed, an MSBuild node deadlocked on a locked output) has no self-timeout, so an unbounded WaitForExit here is a
    // silent infinite hang on the connect path. Bounded, and the process is killed when it overruns.
    private static readonly TimeSpan BuildCeiling = ResolveBuildCeiling();

    private static TimeSpan ResolveBuildCeiling()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("CRLSP_BUILD_TIMEOUT_SECONDS"), out int s) && s > 0)
            return TimeSpan.FromSeconds(s);
        return TimeSpan.FromMinutes(10); // a cold restore + full Release build of the daemon, with room to spare
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
            // Drain BOTH pipes asynchronously: a synchronous ReadToEnd on one while the other fills its buffer deadlocks
            // the child (a classic redirect hang) — which no timeout below could distinguish from a slow build.
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit((int)BuildCeiling.TotalMilliseconds))
            {
                log($"{label} build exceeded {BuildCeiling.TotalMinutes:0} min — killing it (build the plugin manually to see why)");
                try { p.Kill(entireProcessTree: true); } catch { }
                return;
            }
            _ = outTask;
            if (p.ExitCode != 0) log($"{label} build failed (exit {p.ExitCode}): {Drain(errTask)}");
        }
        catch (Exception ex) { log($"{label} build error: {ex.Message}"); }
        finally { if (held) { try { buildLock.ReleaseMutex(); } catch { } } }
    }

    // The child has exited, so both pipes are at EOF and this completes at once; the wait is only a formality.
    private static string Drain(Task<string> pipe)
    {
        try { return pipe.Wait(TimeSpan.FromSeconds(5)) ? pipe.Result : "(output unavailable)"; }
        catch { return "(output unavailable)"; }
    }
}
