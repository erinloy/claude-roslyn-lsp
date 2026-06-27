using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// The codebase operations that run against the ONE shared Roslyn daemon — references / rename / format / symbol-resolve
/// / document-symbols — expressed once as LSP requests over a <see cref="RoslynDaemonClient"/>. The refactor MCP and the
/// `crlsp` CLI are both thin presentation layers over this, so the request mapping and on-disk edit application live in
/// exactly one place (no parallel implementations).
/// </summary>
public static class RoslynOps
{
    public readonly record struct RefLoc(string Path, int Line, int Col);
    public readonly record struct SymbolHit(string Name, string Container, int Kind, string Path, int Line, int Col);
    public readonly record struct DocSymbol(string Name, int Kind, int Line, int Col, int Depth);
    public readonly record struct Diag(int Line, int Col, int Severity, string Code, string Message);

    /// <summary>Pull diagnostics for one document (textDocument/diagnostic — Roslyn serves PULL, not push). Roslyn only
    /// computes diagnostics for OPEN documents, so we didOpen the file's current disk content, pull, then didClose.
    /// Returns the items from a full report; an "unchanged" report (no items) yields an empty list.</summary>
    public static async Task<IReadOnlyList<Diag>> DiagnosticsAsync(
        RoslynDaemonClient client, string file, CancellationToken ct)
    {
        string uri = LspEdits.PathToUri(file);
        string text = File.Exists(file) ? File.ReadAllText(file) : "";
        string langId = file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ? "vb" : "csharp";
        client.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = langId, ["version"] = 1, ["text"] = text },
        });
        try
        {
            JsonNode? res = await client.RequestAsync("textDocument/diagnostic",
                new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, ct).ConfigureAwait(false);
            return ParseDiagnostics(res);
        }
        finally
        {
            client.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
        }
    }

    private static IReadOnlyList<Diag> ParseDiagnostics(JsonNode? res)
    {
        var list = new List<Diag>();
        if (res?["items"] is JsonArray arr)
            foreach (JsonNode? d in arr)
            {
                JsonNode? start = d?["range"]?["start"];
                list.Add(new Diag(
                    start?["line"]?.GetValue<int>() ?? -1,
                    start?["character"]?.GetValue<int>() ?? -1,
                    d?["severity"]?.GetValue<int>() ?? 0,
                    d?["code"]?.ToString() ?? "",
                    d?["message"]?.GetValue<string>() ?? ""));
            }
        return list;
    }

    /// <summary>Every reference to the symbol at a 0-based (line, col) in a file, including the declaration.</summary>
    public static async Task<IReadOnlyList<RefLoc>> FindReferencesAsync(
        RoslynDaemonClient client, string file, int line, int col, CancellationToken ct)
    {
        JsonNode? res = await client.RequestAsync("textDocument/references", new JsonObject
        {
            ["textDocument"] = Doc(file),
            ["position"] = Pos(line, col),
            ["context"] = new JsonObject { ["includeDeclaration"] = true },
        }, ct).ConfigureAwait(false);

        var list = new List<RefLoc>();
        if (res is JsonArray arr)
            foreach (JsonNode? loc in arr)
            {
                JsonNode? start = loc?["range"]?["start"];
                list.Add(new RefLoc(
                    LspEdits.UriToPath(loc?["uri"]?.GetValue<string>() ?? ""),
                    start?["line"]?.GetValue<int>() ?? -1,
                    start?["character"]?.GetValue<int>() ?? -1));
            }
        return list;
    }

    /// <summary>Rename the symbol at a 0-based (line, col) solution-wide; writes edits to disk. Returns changed files.</summary>
    public static async Task<IReadOnlyList<string>> RenameAsync(
        RoslynDaemonClient client, string file, int line, int col, string newName, CancellationToken ct)
    {
        JsonNode? edit = await client.RequestAsync("textDocument/rename", new JsonObject
        {
            ["textDocument"] = Doc(file),
            ["position"] = Pos(line, col),
            ["newName"] = newName,
        }, ct).ConfigureAwait(false);
        return LspEdits.ApplyWorkspaceEdit(edit);
    }

    /// <summary>Rename at an explicit (uri, position) — used after resolving a name to a declaration location.</summary>
    public static async Task<IReadOnlyList<string>> RenameAtAsync(
        RoslynDaemonClient client, string uri, JsonNode position, string newName, CancellationToken ct)
    {
        JsonNode? edit = await client.RequestAsync("textDocument/rename", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = position.DeepClone(),
            ["newName"] = newName,
        }, ct).ConfigureAwait(false);
        return LspEdits.ApplyWorkspaceEdit(edit);
    }

    /// <summary>Reformat a whole document; writes to disk. Returns true if it changed.</summary>
    public static async Task<bool> FormatAsync(RoslynDaemonClient client, string file, CancellationToken ct)
    {
        JsonNode? res = await client.RequestAsync("textDocument/formatting", new JsonObject
        {
            ["textDocument"] = Doc(file),
            ["options"] = new JsonObject { ["tabSize"] = 4, ["insertSpaces"] = true },
        }, ct).ConfigureAwait(false);
        return res is JsonArray edits && edits.Count > 0 && LspEdits.ApplyTextEdits(file, edits);
    }

    /// <summary>Search the workspace for symbols matching a name/query (workspace/symbol).</summary>
    public static async Task<IReadOnlyList<SymbolHit>> WorkspaceSymbolsAsync(
        RoslynDaemonClient client, string query, CancellationToken ct)
    {
        JsonNode? res = await client.RequestAsync("workspace/symbol", new JsonObject { ["query"] = query }, ct).ConfigureAwait(false);
        var list = new List<SymbolHit>();
        if (res is JsonArray arr)
            foreach (JsonNode? s in arr)
            {
                JsonNode? loc = s?["location"];
                JsonNode? start = loc?["range"]?["start"];
                list.Add(new SymbolHit(
                    s?["name"]?.GetValue<string>() ?? "",
                    s?["containerName"]?.GetValue<string>() ?? "",
                    s?["kind"]?.GetValue<int>() ?? 0,
                    LspEdits.UriToPath(loc?["uri"]?.GetValue<string>() ?? ""),
                    start?["line"]?.GetValue<int>() ?? -1,
                    start?["character"]?.GetValue<int>() ?? -1));
            }
        return list;
    }

    /// <summary>Resolve a fully-qualified type/namespace name to its declaration (uri, position) via workspace/symbol.</summary>
    public static async Task<(string uri, JsonNode position)?> ResolveByNameAsync(
        RoslynDaemonClient client, string fqn, CancellationToken ct)
    {
        string shortName = fqn.Contains('.') ? fqn[(fqn.LastIndexOf('.') + 1)..] : fqn;
        JsonNode? res = await client.RequestAsync("workspace/symbol", new JsonObject { ["query"] = shortName }, ct).ConfigureAwait(false);
        if (res is not JsonArray arr) return null;

        JsonNode? best = null;
        foreach (JsonNode? s in arr)
        {
            if (!string.Equals(s?["name"]?.GetValue<string>(), shortName, StringComparison.Ordinal)) continue;
            string container = s?["containerName"]?.GetValue<string>() ?? "";
            string candidateFqn = string.IsNullOrEmpty(container) ? shortName : $"{container}.{shortName}";
            if (string.Equals(candidateFqn, fqn, StringComparison.Ordinal)) { best = s; break; }
            best ??= s;
        }
        JsonNode? location = best?["location"];
        string? uri = location?["uri"]?.GetValue<string>();
        JsonNode? start = location?["range"]?["start"];
        return uri is not null && start is not null ? (uri, start.DeepClone()) : null;
    }

    /// <summary>The symbol outline of one document (textDocument/documentSymbol), flattened with nesting depth.</summary>
    public static async Task<IReadOnlyList<DocSymbol>> DocumentSymbolsAsync(
        RoslynDaemonClient client, string file, CancellationToken ct)
    {
        JsonNode? res = await client.RequestAsync("textDocument/documentSymbol", new JsonObject { ["textDocument"] = Doc(file) }, ct).ConfigureAwait(false);
        var list = new List<DocSymbol>();
        if (res is JsonArray arr) Flatten(arr, 0, list);
        return list;
    }

    private static void Flatten(JsonArray nodes, int depth, List<DocSymbol> into)
    {
        foreach (JsonNode? n in nodes)
        {
            // Hierarchical DocumentSymbol has selectionRange/range; flat SymbolInformation has location.range.
            JsonNode? start = n?["selectionRange"]?["start"] ?? n?["range"]?["start"] ?? n?["location"]?["range"]?["start"];
            into.Add(new DocSymbol(
                n?["name"]?.GetValue<string>() ?? "",
                n?["kind"]?.GetValue<int>() ?? 0,
                start?["line"]?.GetValue<int>() ?? -1,
                start?["character"]?.GetValue<int>() ?? -1,
                depth));
            if (n?["children"] is JsonArray kids) Flatten(kids, depth + 1, into);
        }
    }

    private static JsonObject Doc(string file) => new() { ["uri"] = LspEdits.PathToUri(file) };
    private static JsonObject Pos(int line, int col) => new() { ["line"] = line, ["character"] = col };
}
