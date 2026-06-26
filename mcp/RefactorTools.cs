using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// Roslyn-powered codebase mutations exposed to the agent as MCP tools — the operations Claude Code's read-only LSP tool
/// can't do: solution-wide rename, reference discovery, formatting. Each drives the real Roslyn engine (the same one the
/// IDE "Rename" uses) and writes the resulting edits to disk.
/// </summary>
[McpServerToolType]
public sealed class RefactorTools
{
    private readonly WorkspaceHost _host;

    public RefactorTools(WorkspaceHost host) => _host = host;

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
        Solution solution = await _host.GetSolutionAsync(ct).ConfigureAwait(false);
        Document? doc = WorkspaceHost.FindDocument(solution, filePath);
        if (doc is null) return Err($"file not loaded in the workspace: {filePath}");

        SourceText text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        int position = WorkspaceHost.PositionOf(text, line, character);
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, position, ct).ConfigureAwait(false);
        if (symbol is null) return Err($"no symbol at {filePath}:{line}:{character}");

        return await RenameAsync(solution, symbol, newName, ct).ConfigureAwait(false);
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
        Solution solution = await _host.GetSolutionAsync(ct).ConfigureAwait(false);
        ISymbol? symbol = await ResolveByNameAsync(solution, fullyQualifiedName, ct).ConfigureAwait(false);
        if (symbol is null) return Err($"could not resolve a type or namespace named '{fullyQualifiedName}'");
        return await RenameAsync(solution, symbol, newName, ct).ConfigureAwait(false);
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
        Solution solution = await _host.GetSolutionAsync(ct).ConfigureAwait(false);
        Document? doc = WorkspaceHost.FindDocument(solution, filePath);
        if (doc is null) return Err($"file not loaded in the workspace: {filePath}");

        SourceText text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        int position = WorkspaceHost.PositionOf(text, line, character);
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, position, ct).ConfigureAwait(false);
        if (symbol is null) return Err($"no symbol at {filePath}:{line}:{character}");

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine($"references to {symbol.ToDisplayString()}:");
        int count = 0;
        foreach (ReferencedSymbol r in refs)
        {
            foreach (ReferenceLocation loc in r.Locations)
            {
                FileLinePositionSpan span = loc.Location.GetLineSpan();
                sb.AppendLine($"  {span.Path}:{span.StartLinePosition.Line}:{span.StartLinePosition.Character}");
                count++;
            }
        }
        sb.AppendLine($"({count} reference(s))");
        return sb.ToString();
    }

    [McpServerTool(Name = "format_document")]
    [Description("Reformat a C#/VB file using Roslyn's formatter (whitespace, indentation, spacing) and write it to disk. " +
                 "Returns whether the file changed.")]
    public async Task<string> FormatDocument(
        [Description("Absolute path to the .cs/.vb file.")] string filePath,
        CancellationToken ct)
    {
        Solution solution = await _host.GetSolutionAsync(ct).ConfigureAwait(false);
        Document? doc = WorkspaceHost.FindDocument(solution, filePath);
        if (doc is null) return Err($"file not loaded in the workspace: {filePath}");

        Document formatted = await Formatter.FormatAsync(doc, cancellationToken: ct).ConfigureAwait(false);
        SourceText before = await doc.GetTextAsync(ct).ConfigureAwait(false);
        SourceText after = await formatted.GetTextAsync(ct).ConfigureAwait(false);
        if (before.ContentEquals(after)) return $"no formatting changes: {filePath}";

        File.WriteAllText(doc.FilePath!, after.ToString());
        return $"formatted (written): {filePath}";
    }

    private async Task<string> RenameAsync(Solution solution, ISymbol symbol, string newName, CancellationToken ct)
    {
        var options = new SymbolRenameOptions(
            RenameOverloads: true, RenameInStrings: false, RenameInComments: false, RenameFile: false);
        Solution updated = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, ct).ConfigureAwait(false);

        IReadOnlyList<string> changed = WorkspaceHost.WriteChangedDocuments(solution, updated, ct);
        await _host.ReloadAsync(ct).ConfigureAwait(false); // disk changed — refresh the warm workspace

        var sb = new StringBuilder();
        sb.AppendLine($"renamed {symbol.Kind} '{symbol.Name}' → '{newName}' across {changed.Count} file(s):");
        foreach (var f in changed) sb.AppendLine($"  {f}");
        return sb.ToString();
    }

    /// <summary>Resolve a fully-qualified name to a type (via metadata name) or, failing that, a namespace symbol.</summary>
    private static async Task<ISymbol?> ResolveByNameAsync(Solution solution, string fqn, CancellationToken ct)
    {
        foreach (Project project in solution.Projects)
        {
            Compilation? comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (comp is null) continue;

            INamedTypeSymbol? type = comp.GetTypeByMetadataName(fqn);
            if (type is not null) return type;

            INamespaceSymbol? ns = ResolveNamespace(comp.GlobalNamespace, fqn);
            if (ns is not null) return ns;
        }
        return null;
    }

    private static INamespaceSymbol? ResolveNamespace(INamespaceSymbol root, string dotted)
    {
        INamespaceSymbol current = root;
        foreach (string part in dotted.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            INamespaceSymbol? next = current.GetNamespaceMembers()
                .FirstOrDefault(n => string.Equals(n.Name, part, StringComparison.Ordinal));
            if (next is null) return null;
            current = next;
        }
        return ReferenceEquals(current, root) ? null : current;
    }

    private static string Err(string message) => $"ERROR: {message}";
}
