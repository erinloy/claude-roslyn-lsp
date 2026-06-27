using System.Security.Cryptography;
using System.Text;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// Derives the named-pipe identity that keys a shared daemon to a workspace. Both the daemon and the client link this
/// file, so they hash identically by construction — a client always finds its workspace's daemon.
/// </summary>
public static class PipeKey
{
    /// <summary>Pipe name for a workspace root: stable, collision-resistant, filesystem/pipe-name safe.</summary>
    public static string ForRoot(string root)
    {
        string norm = root.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
        string hex = Convert.ToHexString(hash).ToLowerInvariant()[..16];
        return $"roslyn-lsp-{hex}";
    }

    /// <summary>The election mutex name for a workspace (only the holder spawns the daemon).</summary>
    public static string MutexFor(string pipeName) => $"{pipeName}-spawn";

    /// <summary>The lifetime mutex the daemon holds while alive; the client probes it to detect a live daemon
    /// (shared memory, unlike a pipe, has no connect-or-fail liveness). Must match the daemon's singleton name.</summary>
    public static string DaemonAliveMutex(string pipeName) => $"{pipeName}-daemon";

    /// <summary>Per-direction capacity (bytes) of each client↔daemon Sluice ring. 8 MiB gives headroom for the
    /// largest LSP payloads (full documentSymbol / references over a big solution) plus queued diagnostics.</summary>
    public const long FrameCapacity = 1L << 23;

    /// <summary>JSON key of the hello frame the client sends first (announcing its PID) so the daemon can reap the
    /// session when that process exits — shared memory has no peer-death (pipe-EOF) signal.</summary>
    public const string PidHelloKey = "$claudeRoslynClientPid";

    /// <summary>JSON key of the hello frame the daemon sends back on accept (announcing ITS PID) so the client can detect
    /// the daemon dying — the symmetric peer-death watch. Without it a daemon restart orphans the client's in-flight
    /// requests and the client blocks forever on the dead ring (shared memory has no pipe-EOF). The client intercepts this
    /// frame (never forwards it to Claude), PID-watches the daemon, and exits cleanly on its death so Claude Code restarts
    /// the LSP server and it reconnects to the live daemon.</summary>
    public const string DaemonPidKey = "$claudeRoslynDaemonPid";
}
