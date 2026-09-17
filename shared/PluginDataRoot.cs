namespace ClaudeRoslynLsp;

/// <summary>
/// The ONE resolver for this plugin's DATA ROOT — the directory that holds everything it writes at runtime: the Roslyn
/// server it downloads, daemon / client / MCP / launch logs, extension shadow copies, and (through its PowerShell twin)
/// the launcher's binary shadow copies and the LSP-patch markers.
/// </summary>
/// <remarks>
/// <para>
/// PRECEDENCE, first match wins:
/// <list type="number">
/// <item><c>CLAUDE_PLUGIN_DATA</c> (Claude Code's per-plugin data directory) → <c>&lt;it&gt;\roslyn</c>. Unchanged from
///   before this file existed: every component already used that subdirectory under it, so live daemons keep their logs.</item>
/// <item><c>ZILTCH_DATA_ROOT</c> → <c>&lt;it&gt;\claude-roslyn-lsp</c>.</item>
/// <item><c>Z:\DATA</c> when that directory exists → <c>Z:\DATA\claude-roslyn-lsp</c>.</item>
/// <item>otherwise THROW, naming the variables.</item>
/// </list>
/// Branches 2-4 mirror the Ziltch repo's <c>ZiltchDataRoot.Resolve</c>
/// (<c>src/Reactive.Graph/Reactive.Graph.Abstractions/Storage/ZiltchDataRoot.cs</c>): the override is used verbatim, the
/// preferred root only when it exists, and null / empty / whitespace all mean ABSENT.
/// </para>
/// <para>
/// 🔴 NO APPDATA, AND NO OTHER FALLBACK (Erin, 2026-09-17: "get that kind of stuff out of appdata"). The last branch used
/// to be <c>LocalApplicationData\claude-roslyn-lsp</c>, hand-copied into six call sites across five projects. That same
/// morning a git-bash <c>rm -rf</c> of a backslash path wiped the whole of <c>%LOCALAPPDATA%</c>, and this plugin's MCP
/// server stopped launching. A missing data root is a configuration error to fix, not a location to guess — guessing a
/// user-profile or temp directory is how state ends up somewhere nobody looks and anything can delete.
/// </para>
/// <para>
/// ONE SOURCE, LINKED, NEVER COPIED. No project is referenced by every host (bridge and client do not reference
/// extensions-api), so this file is compiled into each of them with <c>&lt;Compile Include="../shared/PluginDataRoot.cs"
/// Link="PluginDataRoot.cs" /&gt;</c>. It is <c>internal</c> so the copy inside extensions-api never collides with the
/// daemon's and the MCP's own. Its PowerShell twin is <c>boot/data-root.ps1</c>; the two must answer identically.
/// </para>
/// </remarks>
internal static class PluginDataRoot
{
    /// <summary>Claude Code's per-plugin data directory. Wins when set.</summary>
    public const string PluginDataVariable = "CLAUDE_PLUGIN_DATA";

    /// <summary>The Ziltch data root override, shared with every Ziltch participant.</summary>
    public const string ZiltchDataRootVariable = "ZILTCH_DATA_ROOT";

    /// <summary>The preferred data root when it exists on this machine.</summary>
    public const string PreferredRoot = @"Z:\DATA";

    /// <summary>The subdirectory used under <see cref="PluginDataVariable"/>.</summary>
    public const string PluginDataSubdirectory = "roslyn";

    /// <summary>The subdirectory used under <see cref="ZiltchDataRootVariable"/> and <see cref="PreferredRoot"/>.</summary>
    public const string DataRootSubdirectory = "claude-roslyn-lsp";

    /// <summary>
    /// The resolved data root for THIS process. Reads the environment and the filesystem; <see cref="Resolve"/> is the pure
    /// form every precedence case can be tested against.
    /// </summary>
    /// <exception cref="InvalidOperationException">No precedence branch applies.</exception>
    public static string Current => Resolve(
        Environment.GetEnvironmentVariable(PluginDataVariable),
        Environment.GetEnvironmentVariable(ZiltchDataRootVariable),
        Directory.Exists);

    /// <summary>The precedence rule as a pure function of its inputs.</summary>
    /// <param name="pluginData">The <c>CLAUDE_PLUGIN_DATA</c> value; null / empty / whitespace mean absent.</param>
    /// <param name="ziltchDataRoot">The <c>ZILTCH_DATA_ROOT</c> value; null / empty / whitespace mean absent.</param>
    /// <param name="directoryExists">The existence probe for <see cref="PreferredRoot"/>, injected so a test decides it.</param>
    /// <returns>The data root. Never null, never empty.</returns>
    /// <exception cref="InvalidOperationException">No precedence branch applies.</exception>
    public static string Resolve(string? pluginData, string? ziltchDataRoot, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);

        if (!string.IsNullOrWhiteSpace(pluginData))
            return Path.Combine(pluginData, PluginDataSubdirectory);

        if (!string.IsNullOrWhiteSpace(ziltchDataRoot))
            return Path.Combine(ziltchDataRoot, DataRootSubdirectory);

        if (directoryExists(PreferredRoot))
            return Path.Combine(PreferredRoot, DataRootSubdirectory);

        throw new InvalidOperationException(
            $"No claude-roslyn-lsp data root: {PluginDataVariable} and {ZiltchDataRootVariable} are unset and {PreferredRoot} " +
            $"does not exist. Set {ZiltchDataRootVariable} to a data directory (or mount {PreferredRoot[..2]}); Claude Code sets " +
            $"{PluginDataVariable} for the processes it launches. There is deliberately no AppData or temp fallback.");
    }
}
