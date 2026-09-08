using System.Security.Cryptography;
using System.Text;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// Derives the named-pipe identity that keys a shared daemon to a workspace. Both the daemon and the client link this
/// file, so they hash identically by construction — a client always finds its workspace's daemon.
/// </summary>
public static class PipeKey
{
    /// <summary>
    /// The repository root that owns <paramref name="start"/> — the anchor a pipe key is derived from, so that every
    /// session working in one repository lands on ONE daemon.
    ///
    /// <para>🩸 WITHOUT THIS, THE CWD *IS* THE WORKSPACE, AND THE PLUGIN'S OWN "one shared per-workspace server"
    /// CONTRACT IS BROKEN BY ANY SESSION THAT STARTS IN A SUBDIRECTORY. Measured 2026-09-08 on the Ziltch tree — three
    /// cwds inside a SINGLE repo, three different keys, therefore three full Roslyn daemons for one repository:</para>
    /// <code>
    /// Z:\SOURCE\Ziltch\___                      roslyn-lsp-51ee6c5eb9926e5c
    /// Z:\SOURCE\Ziltch\___\src                  roslyn-lsp-d8c1550a6ee21943
    /// Z:\SOURCE\Ziltch\___\src\Reactive.Graph   roslyn-lsp-b638dc695ac4a9fb
    /// </code>
    ///
    /// <para>⚖️ <b>.git IS THE ANCHOR, AND A SOLUTION FILE IS NOT.</b> The same tree carries a <c>.slnx</c> at BOTH
    /// <c>src</c> and <c>src\Reactive.Graph</c>, so a "nearest solution file" rule still splits those two. <c>.git</c>
    /// is the repository boundary by git's own definition (what <c>rev-parse --show-toplevel</c> answers), and NEAREST
    /// is deliberately right rather than outermost: a submodule or a worktree carries its own <c>.git</c> and SHOULD
    /// get its own daemon, because its source content differs.</para>
    ///
    /// <para>🔑 Falls back to <paramref name="start"/> unchanged when nothing is found, so a directory of loose files
    /// behaves exactly as it does today — this collapses same-repository splits and changes nothing else.</para>
    /// </summary>
    public static string ResolveWorkspaceRoot(string start)
    {
        string? solutionFallback = null;
        try
        {
            var dir = new DirectoryInfo(File.Exists(start) ? Path.GetDirectoryName(start)! : start);
            for (; dir is not null; dir = dir.Parent)
            {
                // A worktree/submodule carries .git as a FILE, a normal clone as a directory — either is the boundary.
                string git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return dir.FullName;

                // Remembered, not returned: a .git further up outranks it, and only the OUTERMOST solution file seen
                // on the way up is kept, so two sibling .slnx levels still agree on one root.
                if (dir.EnumerateFiles("*.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
                    solutionFallback = dir.FullName;
            }
        }
        catch { /* an unreadable ancestor must never stop the server from starting — fall through to the input */ }

        return solutionFallback ?? start;
    }

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
