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
}
