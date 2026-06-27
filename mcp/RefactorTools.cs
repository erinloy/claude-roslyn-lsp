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

    [McpServerTool(Name = "get_diagnostics")]
    [Description("Get the Roslyn diagnostics (compiler errors/warnings + analyzer/IDE hints) for one C#/VB file — the same " +
                 "feedback the editor shows. Use it to check a file compiles after an edit, or to find the quick-fixable " +
                 "issues to feed apply_code_action. Returns severity, position, code, and message per item.")]
    public async Task<string> GetDiagnostics(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        IReadOnlyList<RoslynOps.Diag> diags = await RoslynOps.DiagnosticsAsync(client, filePath, ct).ConfigureAwait(false);
        if (diags.Count == 0) return $"no diagnostics: {filePath}";
        var sb = new StringBuilder();
        sb.AppendLine($"diagnostics ({diags.Count}) for {filePath}:");
        foreach (RoslynOps.Diag d in diags)
            sb.AppendLine($"  {Sev(d.Severity),-7} :{d.Line}:{d.Col} {d.Code}: {d.Message}");
        return sb.ToString();
    }

    [McpServerTool(Name = "go_to_definition")]
    [Description("Go to the definition of the symbol at a file position (0-based line/character). Read-only navigation. " +
                 "Returns the defining file:line:col (occasionally several for partials).")]
    public async Task<string> GoToDefinition(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        return Locations("definition", await RoslynOps.DefinitionAsync(client, filePath, line, character, ct).ConfigureAwait(false), filePath, line, character);
    }

    [McpServerTool(Name = "find_implementations")]
    [Description("Find the implementations of the interface, abstract member, or virtual member at a file position " +
                 "(0-based line/character). Read-only. Returns file:line:col of each implementation.")]
    public async Task<string> FindImplementations(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        return Locations("implementations", await RoslynOps.ImplementationsAsync(client, filePath, line, character, ct).ConfigureAwait(false), filePath, line, character);
    }

    [McpServerTool(Name = "type_definition")]
    [Description("Jump to the declared TYPE of the symbol at a file position (e.g. a variable's or parameter's type). " +
                 "Read-only. Returns the type's file:line:col.")]
    public async Task<string> TypeDefinition(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        return Locations("type definition", await RoslynOps.TypeDefinitionAsync(client, filePath, line, character, ct).ConfigureAwait(false), filePath, line, character);
    }

    [McpServerTool(Name = "hover")]
    [Description("Get the hover info — full type signature and XML documentation — for the symbol at a file position " +
                 "(0-based line/character). Read-only. The fastest way to learn a symbol's type/signature without opening files.")]
    public async Task<string> Hover(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        string? hover = await RoslynOps.HoverAsync(client, filePath, line, character, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(hover) ? $"no hover info at {filePath}:{line}:{character}" : hover!;
    }

    [McpServerTool(Name = "list_code_actions")]
    [Description("List the code actions (quick fixes + refactorings) Roslyn offers at a file position or range (0-based). " +
                 "These are the in-place fixes you can then apply by title with apply_code_action — e.g. 'Remove unnecessary " +
                 "usings', 'Add null check', 'Make static', 'Use pattern matching', 'Generate constructor'. Pass endLine to " +
                 "cover a selection. Returns each action's title (apply by exact title) and kind.")]
    public async Task<string> ListCodeActions(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        [Description("Optional 0-based end line for a selection range (defaults to the start line).")] int endLine = -1,
        [Description("Optional 0-based end character for a selection range.")] int endCharacter = -1,
        CancellationToken ct = default)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        int? el = endLine >= 0 ? endLine : null, ec = endCharacter >= 0 ? endCharacter : null;
        IReadOnlyList<RoslynOps.CodeAct> acts = await RoslynOps.CodeActionsAsync(client, filePath, line, character, el, ec, ct).ConfigureAwait(false);
        if (acts.Count == 0) return $"no code actions at {filePath}:{line}:{character}";
        var sb = new StringBuilder();
        sb.AppendLine($"code actions ({acts.Count}) at {filePath}:{line}:{character} — apply by exact title:");
        foreach (RoslynOps.CodeAct a in acts) sb.AppendLine($"  [{(string.IsNullOrEmpty(a.Kind) ? "action" : a.Kind)}] {a.Title}");
        return sb.ToString();
    }

    [McpServerTool(Name = "apply_code_action")]
    [Description("Apply a Roslyn code action (quick fix or refactoring) at a file position/range by its title (from " +
                 "list_code_actions; exact match preferred, otherwise the first title containing the text), writing the " +
                 "edit to disk. This is the efficient in-place way to fix/refactor — let Roslyn produce the edit instead of " +
                 "hand-editing. Returns the changed files.")]
    public async Task<string> ApplyCodeAction(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        [Description("0-based line.")] int line,
        [Description("0-based character/column.")] int character,
        [Description("Title of the action to apply (as shown by list_code_actions).")] string title,
        [Description("Optional 0-based end line for a selection range.")] int endLine = -1,
        [Description("Optional 0-based end character for a selection range.")] int endCharacter = -1,
        CancellationToken ct = default)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        int? el = endLine >= 0 ? endLine : null, ec = endCharacter >= 0 ? endCharacter : null;
        IReadOnlyList<string> changed = await RoslynOps.ApplyCodeActionAsync(client, filePath, line, character, title, el, ec, ct).ConfigureAwait(false);
        if (changed.Count == 0) return Err($"no code action titled '{title}' applied at {filePath}:{line}:{character} (not offered there, or it carried no editable change)");
        return Report($"applied '{title}'", changed);
    }

    [McpServerTool(Name = "organize_imports")]
    [Description("Organize a C# file's using directives — remove unnecessary usings and sort them — via Roslyn, writing to " +
                 "disk. Returns the changed files (empty if already clean).")]
    public async Task<string> OrganizeImports(
        [Description("Absolute path to the .cs file.")] string filePath,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> changed = await RoslynOps.OrganizeImportsAsync(client, filePath, ct).ConfigureAwait(false);
        return changed.Count == 0 ? $"usings already organized: {filePath}" : Report("organized usings", changed);
    }

    [McpServerTool(Name = "document_symbols")]
    [Description("Print the symbol outline of one C#/VB file (namespaces, types, members) indented by nesting — a fast map " +
                 "of a file's structure with the 0-based line/col of each symbol, without reading the whole file.")]
    public async Task<string> DocumentSymbols(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        IReadOnlyList<RoslynOps.DocSymbol> syms = await RoslynOps.DocumentSymbolsAsync(client, filePath, ct).ConfigureAwait(false);
        if (syms.Count == 0) return $"no symbols: {filePath}";
        var sb = new StringBuilder();
        sb.AppendLine($"symbols ({syms.Count}) in {filePath}:");
        foreach (RoslynOps.DocSymbol s in syms) sb.AppendLine($"  {new string(' ', s.Depth * 2)}{s.Name}  :{s.Line}:{s.Col}");
        return sb.ToString();
    }

    [McpServerTool(Name = "search_symbols")]
    [Description("Search the whole workspace for symbols (types, methods, properties) matching a name/query " +
                 "(workspace/symbol). The Roslyn-powered way to locate a symbol across the solution. Returns name, " +
                 "container, and file:line:col.")]
    public async Task<string> SearchSymbols(
        [Description("Symbol name or partial query, e.g. 'GenomeFarm' or 'ClassifyTopology'.")] string query,
        CancellationToken ct)
    {
        RoslynDaemonClient client = await _session.GetAsync(ct).ConfigureAwait(false);
        IReadOnlyList<RoslynOps.SymbolHit> hits = await RoslynOps.WorkspaceSymbolsAsync(client, query, ct).ConfigureAwait(false);
        if (hits.Count == 0) return $"no symbols matching '{query}'";
        var sb = new StringBuilder();
        sb.AppendLine($"symbols matching '{query}' ({hits.Count}):");
        foreach (RoslynOps.SymbolHit h in hits)
            sb.AppendLine($"  {(string.IsNullOrEmpty(h.Container) ? h.Name : $"{h.Container}.{h.Name}")}  {h.Path}:{h.Line}:{h.Col}");
        return sb.ToString();
    }

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
