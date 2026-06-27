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

    // ---- navigation: definition / implementation / type-definition / hover --------------------------------------------

    /// <summary>Go to the definition of the symbol at a 0-based (line, col). Usually one location; can be several (partials).</summary>
    public static async Task<IReadOnlyList<RefLoc>> DefinitionAsync(
        RoslynDaemonClient client, string file, int line, int col, CancellationToken ct)
        => ParseLocations(await client.RequestAsync("textDocument/definition", PosParams(file, line, col), ct).ConfigureAwait(false));

    /// <summary>Find the implementations of the interface/abstract member or type at a 0-based (line, col).</summary>
    public static async Task<IReadOnlyList<RefLoc>> ImplementationsAsync(
        RoslynDaemonClient client, string file, int line, int col, CancellationToken ct)
        => ParseLocations(await client.RequestAsync("textDocument/implementation", PosParams(file, line, col), ct).ConfigureAwait(false));

    /// <summary>Jump to the declared TYPE of the symbol at a 0-based (line, col) (e.g. a variable's type).</summary>
    public static async Task<IReadOnlyList<RefLoc>> TypeDefinitionAsync(
        RoslynDaemonClient client, string file, int line, int col, CancellationToken ct)
        => ParseLocations(await client.RequestAsync("textDocument/typeDefinition", PosParams(file, line, col), ct).ConfigureAwait(false));

    /// <summary>Hover info (type signature + XML doc) for the symbol at a 0-based (line, col). Null if none.</summary>
    public static async Task<string?> HoverAsync(
        RoslynDaemonClient client, string file, int line, int col, CancellationToken ct)
    {
        JsonNode? res = await client.RequestAsync("textDocument/hover", PosParams(file, line, col), ct).ConfigureAwait(false);
        JsonNode? c = res?["contents"];
        if (c is JsonObject o) return o["value"]?.GetValue<string>() ?? o.ToJsonString();       // MarkupContent {kind,value}
        if (c is JsonArray a) return string.Join("\n", a.Select(x => x?["value"]?.GetValue<string>() ?? x?.ToString()));
        if (c is JsonValue v) return v.ToString();                                              // bare MarkedString
        return null;
    }

    // ---- in-place editing: code actions (quick fixes & refactorings) --------------------------------------------------

    public readonly record struct CodeAct(string Title, string Kind, bool Applicable);

    /// <summary>List the code actions (quick fixes + refactorings) available at a 0-based position/range — the menu you'd
    /// then apply by title with <see cref="ApplyCodeActionAsync"/>. Diagnostics overlapping the range drive the quick-fixes.</summary>
    public static async Task<IReadOnlyList<CodeAct>> CodeActionsAsync(
        RoslynDaemonClient client, string file, int line, int col, int? endLine, int? endCol, CancellationToken ct)
        => await WithOpenDocAsync(client, file, async (uri, diags) =>
        {
            JsonNode? res = await client.RequestAsync("textDocument/codeAction",
                CodeActionParams(uri, line, col, endLine, endCol, diags, null), ct).ConfigureAwait(false);
            var list = new List<CodeAct>();
            if (res is JsonArray arr)
                foreach (JsonNode? a in arr)
                    list.Add(new CodeAct(a?["title"]?.GetValue<string>() ?? "", a?["kind"]?.GetValue<string>() ?? "",
                        a?["edit"] is not null || a?["data"] is not null));
            return (IReadOnlyList<CodeAct>)list;
        }, ct).ConfigureAwait(false);

    /// <summary>Apply the code action whose title matches (exact, else contains) at a 0-based position/range. Resolves the
    /// lazy edit (codeAction/resolve) when Roslyn returns the action without one, then writes the edit to disk. Returns the
    /// changed files (empty if no action matched or the action carried no applicable edit).</summary>
    public static async Task<IReadOnlyList<string>> ApplyCodeActionAsync(
        RoslynDaemonClient client, string file, int line, int col, string title, int? endLine, int? endCol, CancellationToken ct)
        => await WithOpenDocAsync(client, file, async (uri, diags) =>
        {
            JsonNode? res = await client.RequestAsync("textDocument/codeAction",
                CodeActionParams(uri, line, col, endLine, endCol, diags, null), ct).ConfigureAwait(false);
            JsonNode? chosen = null;
            if (res is JsonArray arr)
                foreach (JsonNode? a in arr)
                {
                    string t = a?["title"]?.GetValue<string>() ?? "";
                    if (string.Equals(t, title, StringComparison.OrdinalIgnoreCase)) { chosen = a; break; }
                    if (chosen is null && t.Contains(title, StringComparison.OrdinalIgnoreCase)) chosen = a;
                }
            return (IReadOnlyList<string>)await ResolveAndApplyAsync(client, chosen, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    /// <summary>Organize/clean a file's using directives (remove unnecessary + sort) via the source.organizeImports action.</summary>
    public static async Task<IReadOnlyList<string>> OrganizeImportsAsync(
        RoslynDaemonClient client, string file, CancellationToken ct)
        => await WithOpenDocAsync(client, file, async (uri, diags) =>
        {
            JsonNode? res = await client.RequestAsync("textDocument/codeAction",
                CodeActionParams(uri, 0, 0, 1_000_000, 0, diags, "source.organizeImports"), ct).ConfigureAwait(false);
            var changed = new List<string>();
            if (res is JsonArray arr)
                foreach (JsonNode? a in arr)
                    foreach (string f in await ResolveAndApplyAsync(client, a, ct).ConfigureAwait(false))
                        if (!changed.Contains(f)) changed.Add(f);
            return (IReadOnlyList<string>)changed;
        }, ct).ConfigureAwait(false);

    private static async Task<IReadOnlyList<string>> ResolveAndApplyAsync(RoslynDaemonClient client, JsonNode? action, CancellationToken ct)
    {
        if (action is null) return Array.Empty<string>();
        // A Roslyn code action usually arrives with `data` and a lazy `edit`; codeAction/resolve fills the edit in.
        if (action["edit"] is null && action["data"] is not null)
        {
            JsonNode? resolved = await client.RequestAsync("codeAction/resolve", action.DeepClone()!.AsObject(), ct).ConfigureAwait(false);
            if (resolved is not null) action = resolved;
        }
        return action["edit"] is not null ? LspEdits.ApplyWorkspaceEdit(action["edit"]) : Array.Empty<string>();
    }

    // ---- shared helpers ----------------------------------------------------------------------------------------------

    /// <summary>Parse a definition/implementation/typeDefinition result: Location | Location[] | LocationLink | LocationLink[].</summary>
    private static List<RefLoc> ParseLocations(JsonNode? res)
    {
        var list = new List<RefLoc>();
        void AddOne(JsonNode? loc)
        {
            if (loc is null) return;
            string uri = loc["uri"]?.GetValue<string>() ?? loc["targetUri"]?.GetValue<string>() ?? "";
            JsonNode? start = loc["range"]?["start"] ?? loc["targetSelectionRange"]?["start"] ?? loc["targetRange"]?["start"];
            if (uri.Length == 0 || start is null) return;
            list.Add(new RefLoc(LspEdits.UriToPath(uri), start["line"]?.GetValue<int>() ?? -1, start["character"]?.GetValue<int>() ?? -1));
        }
        if (res is JsonArray arr) foreach (JsonNode? l in arr) AddOne(l);
        else AddOne(res);
        return list;
    }

    /// <summary>didOpen a file (Roslyn computes code actions / diagnostics only for OPEN docs), pull its diagnostics for
    /// code-action context, run <paramref name="body"/>, then didClose. Disk stays the single source of truth.</summary>
    private static async Task<T> WithOpenDocAsync<T>(
        RoslynDaemonClient client, string file, Func<string, JsonArray, Task<T>> body, CancellationToken ct)
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
            JsonNode? dres = await client.RequestAsync("textDocument/diagnostic",
                new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, ct).ConfigureAwait(false);
            JsonArray diags = (dres?["items"] as JsonArray)?.DeepClone()?.AsArray() ?? new JsonArray();
            return await body(uri, diags).ConfigureAwait(false);
        }
        finally
        {
            client.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
        }
    }

    private static JsonObject CodeActionParams(string uri, int line, int col, int? endLine, int? endCol, JsonArray allDiags, string? only)
    {
        int el = endLine ?? line, ec = endCol ?? col;
        // Pass the diagnostics overlapping the requested line span as context — that's what surfaces the quick-fixes.
        var ctxDiags = new JsonArray();
        foreach (JsonNode? d in allDiags)
        {
            int dl = d?["range"]?["start"]?["line"]?.GetValue<int>() ?? -1;
            if (dl >= line && dl <= el) ctxDiags.Add(d!.DeepClone());
        }
        var context = new JsonObject { ["diagnostics"] = ctxDiags };
        if (only is not null) context["only"] = new JsonArray { only };
        return new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = col },
                ["end"] = new JsonObject { ["line"] = el, ["character"] = ec },
            },
            ["context"] = context,
        };
    }

    private static JsonObject PosParams(string file, int line, int col)
        => new() { ["textDocument"] = Doc(file), ["position"] = Pos(line, col) };

    private static JsonObject Doc(string file) => new() { ["uri"] = LspEdits.PathToUri(file) };
    private static JsonObject Pos(int line, int col) => new() { ["line"] = line, ["character"] = col };
}
