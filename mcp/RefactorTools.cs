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
/// Every one of them runs under <see cref="Bounded"/> — the single hard ceiling on a tool call.
/// </summary>
[McpServerToolType]
public sealed class RefactorTools
{
    private readonly DaemonSession _session;

    public RefactorTools(DaemonSession session) => _session = session;

    // ---- the hard ceiling --------------------------------------------------------------------------------------------
    //
    // Every inner path is already individually bounded (the per-request timeout, the connect poll, the first-run build).
    // "A tool call cannot hang" must not DEPEND on all of those staying correct forever, though: one new unbounded await
    // anywhere below would silently reintroduce the failure — an agent parked for hours on a call that will never return,
    // with no signal that anything is wrong. So the ceiling is also enforced HERE, once, around the whole call, where it
    // holds no matter what the layers below do. Override with CRLSP_TOOL_CEILING_SECONDS; <=0 disables it.
    private static readonly TimeSpan ToolCeiling = ResolveToolCeiling();

    private static TimeSpan ResolveToolCeiling()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("CRLSP_TOOL_CEILING_SECONDS"), out int s))
            return s > 0 ? TimeSpan.FromSeconds(s) : Timeout.InfiniteTimeSpan; // <=0 -> no bound (explicit escape hatch)
        return TimeSpan.FromSeconds(300); // above the 180s request timeout, so the specific inner reason is reported first
    }

    /// <summary>Run one tool call against the shared daemon under the hard ceiling, returning an error string on overrun.</summary>
    /// <remarks>On any overrun the cached connection is dropped: a bounded call that leaves a wedged link in place merely
    /// converts one hang into an unending series of failures. The next call reconnects (restarting the daemon if it died).</remarks>
    private async Task<string> Bounded(
        string tool, Func<RoslynDaemonClient, CancellationToken, Task<string>> body, CancellationToken ct)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (ToolCeiling != Timeout.InfiniteTimeSpan) call.CancelAfter(ToolCeiling);
        try
        {
            RoslynDaemonClient client = await _session.GetAsync(call.Token).ConfigureAwait(false);
            return await body(client, call.Token).ConfigureAwait(false);
        }
        // 🩸 THE HOST CANCELS FIRST, AND THAT USED TO SKIP THE DROP ENTIRELY — the defect this whole guard existed to
        // prevent. ToolCeiling is 300 s; the MCP host's own tool timeout on this fleet is 120 s. So the host ALWAYS
        // wins the race, `ct.IsCancellationRequested` is true, the old `when (!ct.IsCancellationRequested)` filter
        // excluded the catch, and Invalidate() never ran. The wedged connection stayed cached for the life of the
        // session — "one hang converted into an unending series of failures", which is verbatim what the remark above
        // says this must not do.
        //
        // 📏 MEASURED 2026-09-09 by @testy, who could not recover and could not restart (MCP servers attach at SESSION
        // START, so a poisoned cache is unrecoverable for the whole session): two calls, minutes apart, BOTH kinds:
        //     search_symbols "LocalGraph"   workspace/symbol         timed out at 120 s
        //     get_diagnostics <real .cs>    textDocument/diagnostic  timed out at 120 s
        // while the same daemon answered other clients sub-second in the same window. Five hypotheses were measured
        // and killed chasing why the DAEMON was at fault. It was not; the client never dropped its dead link.
        //
        // ⚖️ SO INVALIDATE ON EVERY CANCELLATION, and the asymmetry is the whole argument: dropping a HEALTHY
        // connection costs one reconnect on the next call. NOT dropping a wedged one costs every remaining LSP call in
        // the session, with no recovery path the agent can reach. Those are not comparable, so the doubtful case takes
        // the cheap side. Host cancellation is still rethrown afterwards, because whether the caller gave up is not
        // ours to reinterpret.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)   // the HOST gave up (or the user did)
        {
            _session.Invalidate();
            throw;   // cancellation semantics are the caller's; the drop is ours
        }
        catch (OperationCanceledException)                                     // ours: the hard ceiling tripped
        {
            _session.Invalidate();
            return Err($"'{tool}' hit the {ToolCeiling.TotalSeconds:0}s hard ceiling and was aborted — the daemon took the " +
                       "work and never finished it. The connection was dropped; retry to reconnect (the daemon restarts if " +
                       "it died). Raise CRLSP_TOOL_CEILING_SECONDS if this solution genuinely needs longer.");
        }
        catch (TimeoutException ex) { _session.Invalidate(); return Err($"'{tool}': {ex.Message}"); }
        catch (IOException ex) { _session.Invalidate(); return Err($"'{tool}': {ex.Message} — connection dropped; retry to reconnect"); }
    }

    // ---- tools -------------------------------------------------------------------------------------------------------

    [McpServerTool(Name = "rename_symbol")]
    [Description("Rename the symbol at a file position (0-based line/character) everywhere it is used across the whole " +
                 "solution, then write the edits to disk. Use this for classes, methods, properties, fields, locals, " +
                 "parameters — any symbol. Returns the list of changed files.")]
    public Task<string> RenameSymbol(
        [Description("Absolute path to the .cs/.vb file containing the symbol.")] string filePath,
        [Description("0-based line of the symbol occurrence.")] int line,
        [Description("0-based character/column of the symbol occurrence.")] int character,
        [Description("The new name (identifier only, no namespace).")] string newName,
        CancellationToken ct)
        => Bounded("rename_symbol", async (client, tk) =>
        {
            IReadOnlyList<string> changed = await RoslynOps.RenameAsync(client, filePath, line, character, newName, tk).ConfigureAwait(false);
            if (changed.Count == 0) return Err($"no rename produced at {filePath}:{line}:{character} (no symbol there, or no change)");
            return Report($"renamed → '{newName}'", changed);
        }, ct);

    [McpServerTool(Name = "rename_symbol_by_name")]
    [Description("Rename a type or namespace identified by its fully-qualified name (e.g. 'My.Ns.OldClass' or a namespace " +
                 "'My.Old.Namespace') to a new short name, solution-wide, writing edits to disk. Prefer rename_symbol " +
                 "when you have a file position; use this when you only know the qualified name.")]
    public Task<string> RenameSymbolByName(
        [Description("Fully-qualified type or namespace name, e.g. 'My.Ns.OldClass'.")] string fullyQualifiedName,
        [Description("The new short name (identifier only).")] string newName,
        CancellationToken ct)
        => Bounded("rename_symbol_by_name", async (client, tk) =>
        {
            (string uri, System.Text.Json.Nodes.JsonNode position)? loc =
                await RoslynOps.ResolveByNameAsync(client, fullyQualifiedName, tk).ConfigureAwait(false);
            if (loc is null) return Err($"could not resolve a type or namespace named '{fullyQualifiedName}'");

            IReadOnlyList<string> changed = await RoslynOps.RenameAtAsync(client, loc.Value.uri, loc.Value.position, newName, tk).ConfigureAwait(false);
            if (changed.Count == 0) return Err($"no rename produced for '{fullyQualifiedName}'");
            return Report($"renamed '{fullyQualifiedName}' → '{newName}'", changed);
        }, ct);

    [McpServerTool(Name = "find_references")]
    [Description("Find every reference to the symbol at a file position (0-based line/character) across the solution. " +
                 "Read-only — use it to preview the blast radius before a rename. Returns file:line:col locations.")]
    public Task<string> FindReferences(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
        => Bounded("find_references", async (client, tk) =>
        {
            IReadOnlyList<RoslynOps.RefLoc> refs = await RoslynOps.FindReferencesAsync(client, filePath, line, character, tk).ConfigureAwait(false);
            if (refs.Count == 0) return $"no references found at {filePath}:{line}:{character}";

            var sb = new StringBuilder();
            sb.AppendLine($"references ({refs.Count}):");
            foreach (RoslynOps.RefLoc r in refs) sb.AppendLine($"  {r.Path}:{r.Line}:{r.Col}");
            return sb.ToString();
        }, ct);

    [McpServerTool(Name = "format_document")]
    [Description("Reformat a C#/VB file using Roslyn's formatter (whitespace, indentation, spacing) and write it to disk. " +
                 "Returns whether the file changed.")]
    public Task<string> FormatDocument(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        CancellationToken ct)
        => Bounded("format_document", async (client, tk) =>
        {
            bool changed = await RoslynOps.FormatAsync(client, filePath, tk).ConfigureAwait(false);
            return changed ? $"formatted (written): {filePath}" : $"no formatting changes: {filePath}";
        }, ct);

    [McpServerTool(Name = "get_diagnostics")]
    [Description("Get the Roslyn diagnostics (compiler errors/warnings + analyzer/IDE hints) for one C#/VB file — the same " +
                 "feedback the editor shows. Use it to check a file compiles after an edit, or to find the quick-fixable " +
                 "issues to feed apply_code_action. Returns severity, position, code, and message per item.")]
    public Task<string> GetDiagnostics(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        CancellationToken ct)
        => Bounded("get_diagnostics", async (client, tk) =>
        {
            IReadOnlyList<RoslynOps.Diag> diags = await RoslynOps.DiagnosticsAsync(client, filePath, tk).ConfigureAwait(false);
            if (diags.Count == 0) return $"no diagnostics: {filePath}";
            var sb = new StringBuilder();
            sb.AppendLine($"diagnostics ({diags.Count}) for {filePath}:");
            foreach (RoslynOps.Diag d in diags)
                sb.AppendLine($"  {Sev(d.Severity),-7} :{d.Line}:{d.Col} {d.Code}: {d.Message}");
            return sb.ToString();
        }, ct);

    [McpServerTool(Name = "go_to_definition")]
    [Description("Go to the definition of the symbol at a file position (0-based line/character). Read-only navigation. " +
                 "Returns the defining file:line:col (occasionally several for partials).")]
    public Task<string> GoToDefinition(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
        => Bounded("go_to_definition", async (client, tk) =>
            Locations("definition", await RoslynOps.DefinitionAsync(client, filePath, line, character, tk).ConfigureAwait(false), filePath, line, character), ct);

    [McpServerTool(Name = "find_implementations")]
    [Description("Find the implementations of the interface, abstract member, or virtual member at a file position " +
                 "(0-based line/character). Read-only. Returns file:line:col of each implementation.")]
    public Task<string> FindImplementations(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
        => Bounded("find_implementations", async (client, tk) =>
            Locations("implementations", await RoslynOps.ImplementationsAsync(client, filePath, line, character, tk).ConfigureAwait(false), filePath, line, character), ct);

    [McpServerTool(Name = "type_definition")]
    [Description("Jump to the declared TYPE of the symbol at a file position (e.g. a variable's or parameter's type). " +
                 "Read-only. Returns the type's file:line:col.")]
    public Task<string> TypeDefinition(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
        => Bounded("type_definition", async (client, tk) =>
            Locations("type definition", await RoslynOps.TypeDefinitionAsync(client, filePath, line, character, tk).ConfigureAwait(false), filePath, line, character), ct);

    [McpServerTool(Name = "hover")]
    [Description("Get the hover info — full type signature and XML documentation — for the symbol at a file position " +
                 "(0-based line/character). Read-only. The fastest way to learn a symbol's type/signature without opening files.")]
    public Task<string> Hover(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
        => Bounded("hover", async (client, tk) =>
        {
            string? hover = await RoslynOps.HoverAsync(client, filePath, line, character, tk).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(hover) ? $"no hover info at {filePath}:{line}:{character}" : hover!;
        }, ct);

    [McpServerTool(Name = "list_code_actions")]
    [Description("List the code actions (quick fixes + refactorings) Roslyn offers at a file position or range (0-based). " +
                 "These are the in-place fixes you can then apply by title with apply_code_action — e.g. 'Remove unnecessary " +
                 "usings', 'Add null check', 'Make static', 'Use pattern matching', 'Generate constructor'. Pass endLine to " +
                 "cover a selection. Returns each action's title (apply by exact title) and kind.")]
    public Task<string> ListCodeActions(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        [Description("Optional 0-based end line for a selection range (defaults to the start line).")] int endLine = -1,
        [Description("Optional 0-based end character for a selection range.")] int endCharacter = -1,
        CancellationToken ct = default)
        => Bounded("list_code_actions", async (client, tk) =>
        {
            int? el = endLine >= 0 ? endLine : null, ec = endCharacter >= 0 ? endCharacter : null;
            IReadOnlyList<RoslynOps.CodeAct> acts = await RoslynOps.CodeActionsAsync(client, filePath, line, character, el, ec, tk).ConfigureAwait(false);
            if (acts.Count == 0) return $"no code actions at {filePath}:{line}:{character}";
            var sb = new StringBuilder();
            sb.AppendLine($"code actions ({acts.Count}) at {filePath}:{line}:{character} — apply by exact title:");
            foreach (RoslynOps.CodeAct a in acts) sb.AppendLine($"  [{(string.IsNullOrEmpty(a.Kind) ? "action" : a.Kind)}] {a.Title}");
            return sb.ToString();
        }, ct);

    [McpServerTool(Name = "apply_code_action")]
    [Description("Apply a Roslyn code action (quick fix or refactoring) at a file position/range by its title (from " +
                 "list_code_actions; exact match preferred, otherwise the first title containing the text), writing the " +
                 "edit to disk. This is the efficient in-place way to fix/refactor — let Roslyn produce the edit instead of " +
                 "hand-editing. Returns the changed files.")]
    public Task<string> ApplyCodeAction(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        [Description("Title of the action to apply (as shown by list_code_actions).")] string title,
        [Description("Optional 0-based end line for a selection range.")] int endLine = -1,
        [Description("Optional 0-based end character for a selection range.")] int endCharacter = -1,
        CancellationToken ct = default)
        => Bounded("apply_code_action", async (client, tk) =>
        {
            int? el = endLine >= 0 ? endLine : null, ec = endCharacter >= 0 ? endCharacter : null;
            IReadOnlyList<string> changed = await RoslynOps.ApplyCodeActionAsync(client, filePath, line, character, title, el, ec, tk).ConfigureAwait(false);
            if (changed.Count == 0) return Err($"no code action titled '{title}' applied at {filePath}:{line}:{character} (not offered there, or it carried no editable change)");
            return Report($"applied '{title}'", changed);
        }, ct);

    [McpServerTool(Name = "organize_imports")]
    [Description("Organize a C# file's using directives — remove unnecessary usings and sort them — via Roslyn, writing to " +
                 "disk. Returns the changed files (empty if already clean).")]
    public Task<string> OrganizeImports(
        [Description("Absolute path to the .cs file.")] string filePath,
        CancellationToken ct)
        => Bounded("organize_imports", async (client, tk) =>
        {
            IReadOnlyList<string> changed = await RoslynOps.OrganizeImportsAsync(client, filePath, tk).ConfigureAwait(false);
            return changed.Count == 0 ? $"usings already organized: {filePath}" : Report("organized usings", changed);
        }, ct);

    [McpServerTool(Name = "document_symbols")]
    [Description("Print the symbol outline of one C#/VB file (namespaces, types, members) indented by nesting — a fast map " +
                 "of a file's structure with the 0-based line/col of each symbol, without reading the whole file.")]
    public Task<string> DocumentSymbols(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        CancellationToken ct)
        => Bounded("document_symbols", async (client, tk) =>
        {
            IReadOnlyList<RoslynOps.DocSymbol> syms = await RoslynOps.DocumentSymbolsAsync(client, filePath, tk).ConfigureAwait(false);
            if (syms.Count == 0) return $"no symbols: {filePath}";
            var sb = new StringBuilder();
            sb.AppendLine($"symbols ({syms.Count}) in {filePath}:");
            foreach (RoslynOps.DocSymbol s in syms) sb.AppendLine($"  {new string(' ', s.Depth * 2)}{s.Name}  :{s.Line}:{s.Col}");
            return sb.ToString();
        }, ct);

    [McpServerTool(Name = "search_symbols")]
    [Description("Search the whole workspace for symbols (types, methods, properties) matching a name/query " +
                 "(workspace/symbol). The Roslyn-powered way to locate a symbol across the solution. Returns name, " +
                 "container, and file:line:col.")]
    public Task<string> SearchSymbols(
        [Description("Symbol name or partial query, e.g. 'GenomeFarm' or 'ClassifyTopology'.")] string query,
        CancellationToken ct)
        => Bounded("search_symbols", async (client, tk) =>
        {
            IReadOnlyList<RoslynOps.SymbolHit> hits = await RoslynOps.WorkspaceSymbolsAsync(client, query, tk).ConfigureAwait(false);
            if (hits.Count == 0) return $"no symbols matching '{query}'";
            var sb = new StringBuilder();
            sb.AppendLine($"symbols matching '{query}' ({hits.Count}):");
            foreach (RoslynOps.SymbolHit h in hits)
                sb.AppendLine($"  {(string.IsNullOrEmpty(h.Container) ? h.Name : $"{h.Container}.{h.Name}")}  {h.Path}:{h.Line}:{h.Col}");
            return sb.ToString();
        }, ct);

    private static string Locations(string label, IReadOnlyList<RoslynOps.RefLoc> locs, string file, int line, int col)
    {
        if (locs.Count == 0) return $"no {label} at {file}:{line}:{col}";
        var sb = new StringBuilder();
        sb.AppendLine($"{label} ({locs.Count}):");
        foreach (RoslynOps.RefLoc r in locs) sb.AppendLine($"  {r.Path}:{r.Line}:{r.Col}");
        return sb.ToString();
    }

    private static string Sev(int s) => s switch { 1 => "error", 2 => "warning", 3 => "info", 4 => "hint", _ => "?" };

    private static string Report(string action, IReadOnlyList<string> changed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{action} across {changed.Count} file(s):");
        foreach (string f in changed) sb.AppendLine($"  {f}");
        return sb.ToString();
    }

    private static string Err(string message) => $"ERROR: {message}";
}
