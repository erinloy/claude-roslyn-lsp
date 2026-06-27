using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeRoslynLsp.Bridge;
using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// Roslyn-powered codebase mutations exposed to the agent as MCP tools — the operations Claude Code's read-only LSP tool
/// can't do: solution-wide rename, reference discovery, formatting. Each issues an LSP request to the ONE shared Roslyn
/// daemon (the same warm workspace the LSP uses) and writes the resulting edits to disk — so no per-agent workspace load.
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
        JsonNode? edit = await client.RequestAsync("textDocument/rename", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = LspEdits.PathToUri(filePath) },
            ["position"] = Pos(line, character),
            ["newName"] = newName,
        }, ct).ConfigureAwait(false);

        IReadOnlyList<string> changed = LspEdits.ApplyWorkspaceEdit(edit);
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
        (string uri, JsonNode position)? loc = await ResolveByNameAsync(client, fullyQualifiedName, ct).ConfigureAwait(false);
        if (loc is null) return Err($"could not resolve a type or namespace named '{fullyQualifiedName}'");

        JsonNode? edit = await client.RequestAsync("textDocument/rename", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = loc.Value.uri },
            ["position"] = loc.Value.position,
            ["newName"] = newName,
        }, ct).ConfigureAwait(false);

        IReadOnlyList<string> changed = LspEdits.ApplyWorkspaceEdit(edit);
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
        JsonNode? result = await client.RequestAsync("textDocument/references", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = LspEdits.PathToUri(filePath) },
            ["position"] = Pos(line, character),
            ["context"] = new JsonObject { ["includeDeclaration"] = true },
        }, ct).ConfigureAwait(false);

        if (result is not JsonArray locations || locations.Count == 0)
            return $"no references found at {filePath}:{line}:{character}";

        var sb = new StringBuilder();
        sb.AppendLine($"references ({locations.Count}):");
        foreach (JsonNode? loc in locations)
        {
            string path = LspEdits.UriToPath(loc?["uri"]?.GetValue<string>() ?? "");
            JsonNode? start = loc?["range"]?["start"];
            sb.AppendLine($"  {path}:{start?["line"]?.GetValue<int>()}:{start?["character"]?.GetValue<int>()}");
        }
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
        JsonNode? result = await client.RequestAsync("textDocument/formatting", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = LspEdits.PathToUri(filePath) },
            ["options"] = new JsonObject { ["tabSize"] = 4, ["insertSpaces"] = true },
        }, ct).ConfigureAwait(false);

        if (result is not JsonArray edits || edits.Count == 0) return $"no formatting changes: {filePath}";
        bool changed = LspEdits.ApplyTextEdits(filePath, edits);
        return changed ? $"formatted (written): {filePath}" : $"no formatting changes: {filePath}";
    }

    /// <summary>Resolve a fully-qualified type/namespace name to a declaration location via workspace/symbol.</summary>
    private static async Task<(string uri, JsonNode position)?> ResolveByNameAsync(
        RoslynDaemonClient client, string fqn, CancellationToken ct)
    {
        string shortName = fqn.Contains('.') ? fqn[(fqn.LastIndexOf('.') + 1)..] : fqn;
        JsonNode? syms = await client.RequestAsync("workspace/symbol", new JsonObject { ["query"] = shortName }, ct).ConfigureAwait(false);
        if (syms is not JsonArray arr) return null;

        JsonNode? best = null;
        foreach (JsonNode? s in arr)
        {
            if (!string.Equals(s?["name"]?.GetValue<string>(), shortName, StringComparison.Ordinal)) continue;
            // Prefer an exact FQN match on containerName + name; else keep the first name match as a fallback.
            string container = s?["containerName"]?.GetValue<string>() ?? "";
            string candidateFqn = string.IsNullOrEmpty(container) ? shortName : $"{container}.{shortName}";
            if (string.Equals(candidateFqn, fqn, StringComparison.Ordinal)) { best = s; break; }
            best ??= s;
        }
        if (best?["location"] is not JsonObject location) return null;
        string? uri = location["uri"]?.GetValue<string>();
        JsonNode? start = location["range"]?["start"];
        if (uri is null || start is null) return null;
        return (uri, start.DeepClone());
    }

    private static JsonObject Pos(int line, int character) => new() { ["line"] = line, ["character"] = character };

    private static string Report(string action, IReadOnlyList<string> changed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{action} across {changed.Count} file(s):");
        foreach (string f in changed) sb.AppendLine($"  {f}");
        return sb.ToString();
    }

    private static string Err(string message) => $"ERROR: {message}";
}
