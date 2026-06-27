using System.ComponentModel;
using System.Text;
using ClaudeRoslynLsp.Bridge;
using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// Roslyn-powered codebase mutations exposed to the agent as MCP tools — the operations Claude Code's read-only LSP tool
/// can't do: solution-wide rename, reference discovery, formatting. Thin presentation layer over <see cref="RoslynOps"/>:
/// the request mapping + on-disk edit application live once in the shared bridge, so the MCP and the `crlsp` CLI drive the
/// ONE warm Roslyn daemon through the exact same verified path. These methods just format the result as a tool string.
/// </summary>
[McpServerToolType]
public sealed class RefactorTools
{
    private readonly DaemonSession _session;

    public RefactorTools(DaemonSession session) => _session = session;

    [McpServerTool(Name = "rename_symbol")]
    [Description("Rename the symbol at a file position (0-based line/character) everywhere it is used across the whole " +
                 "solution, then write the edits to disk. Use this for classes, methods, properties, fields, locals, " +
                 "parameters — any symbol. Returns the list of changed files.")]
    public async Task<string> RenameSymbol(
        [Description("Absolute path to the .cs/.vb file containing the symbol.")] string filePath,
        [Description("0-based line of the symbol occurrence.")] int line,
        [Description("0-based character/column of the symbol occurrence.")] int character,
        [Description("The new name (identifier only, no namespace).")] string newName,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> changed = await RoslynOps.RenameAsync(client, filePath, line, character, newName, ct).ConfigureAwait(false);
        if (changed.Count == 0) return Err($"no rename produced at {filePath}:{line}:{character} (no symbol there, or no change)");
        return Report($"renamed → '{newName}'", changed);
    }

    [McpServerTool(Name = "rename_symbol_by_name")]
    [Description("Rename a type or namespace identified by its fully-qualified name (e.g. 'My.Ns.OldClass' or a namespace " +
                 "'My.Old.Namespace') to a new short name, solution-wide, writing edits to disk. Prefer rename_symbol " +
                 "when you have a file position; use this when you only know the qualified name.")]
    public async Task<string> RenameSymbolByName(
        [Description("Fully-qualified type or namespace name, e.g. 'My.Ns.OldClass'.")] string fullyQualifiedName,
        [Description("The new short name (identifier only).")] string newName,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        (string uri, System.Text.Json.Nodes.JsonNode position)? loc =
            await RoslynOps.ResolveByNameAsync(client, fullyQualifiedName, ct).ConfigureAwait(false);
        if (loc is null) return Err($"could not resolve a type or namespace named '{fullyQualifiedName}'");

        IReadOnlyList<string> changed = await RoslynOps.RenameAtAsync(client, loc.Value.uri, loc.Value.position, newName, ct).ConfigureAwait(false);
        if (changed.Count == 0) return Err($"no rename produced for '{fullyQualifiedName}'");
        return Report($"renamed '{fullyQualifiedName}' → '{newName}'", changed);
    }

    [McpServerTool(Name = "find_references")]
    [Description("Find every reference to the symbol at a file position (0-based line/character) across the solution. " +
                 "Read-only — use it to preview the blast radius before a rename. Returns file:line:col locations.")]
    public async Task<string> FindReferences(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        IReadOnlyList<RoslynOps.RefLoc> refs = await RoslynOps.FindReferencesAsync(client, filePath, line, character, ct).ConfigureAwait(false);
        if (refs.Count == 0) return $"no references found at {filePath}:{line}:{character}";

        var sb = new StringBuilder();
        sb.AppendLine($"references ({refs.Count}):");
        foreach (RoslynOps.RefLoc r in refs) sb.AppendLine($"  {r.Path}:{r.Line}:{r.Col}");
        return sb.ToString();
    }

    [McpServerTool(Name = "format_document")]
    [Description("Reformat a C#/VB file using Roslyn's formatter (whitespace, indentation, spacing) and write it to disk. " +
                 "Returns whether the file changed.")]
    public async Task<string> FormatDocument(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        bool changed = await RoslynOps.FormatAsync(client, filePath, ct).ConfigureAwait(false);
        return changed ? $"formatted (written): {filePath}" : $"no formatting changes: {filePath}";
    }

    private static string Report(string action, IReadOnlyList<string> changed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{action} across {changed.Count} file(s):");
        foreach (string f in changed) sb.AppendLine($"  {f}");
        return sb.ToString();
    }

    private static string Err(string message) => $"ERROR: {message}";
}
