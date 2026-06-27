using ClaudeRoslynLsp.Bridge;
using ConsoleAppFramework;

// crlsp — the command-line surface of the ONE shared Roslyn daemon. Each subcommand connects to (or starts) the warm
// workspace daemon for the current repo and drives it with a single LSP request, so a skill/script gets real Roslyn
// answers (references, rename, format, symbols) without ever loading a workspace of its own. Built once + `dotnet exec`'d.
var app = ConsoleApp.Create();
app.Add<CrlspCommands>();
await app.RunAsync(args);

/// <summary>The crlsp verbs. Each is a thin presentation layer over <see cref="RoslynOps"/> (the same verified path the
/// refactor MCP uses) — connect, one request, format the result for a terminal.</summary>
public sealed class CrlspCommands
{
    /// <summary>Find every reference to the symbol at a 0-based file position (declaration included).</summary>
    /// <param name="file">Path to the .cs/.vb file (absolute, or relative to the workspace root).</param>
    /// <param name="line">0-based line of the symbol occurrence.</param>
    /// <param name="col">0-based character/column of the symbol occurrence.</param>
    [Command("refs")]
    public async Task Refs([Argument] string file, [Argument] int line, [Argument] int col, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var refs = await RoslynOps.FindReferencesAsync(c, file, line, col, ct);
        if (refs.Count == 0) { Console.WriteLine($"no references at {file}:{line}:{col}"); return; }
        Console.WriteLine($"references ({refs.Count}):");
        foreach (var r in refs) Console.WriteLine($"  {r.Path}:{r.Line}:{r.Col}");
    }

    /// <summary>Search the whole workspace for symbols matching a name/query (workspace/symbol).</summary>
    /// <param name="query">A name or substring, e.g. "RoslynOps" or "Connect".</param>
    [Command("symbol")]
    public async Task Symbol([Argument] string query, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var hits = await RoslynOps.WorkspaceSymbolsAsync(c, query, ct);
        if (hits.Count == 0) { Console.WriteLine($"no symbols matching '{query}'"); return; }
        Console.WriteLine($"symbols ({hits.Count}):");
        foreach (var s in hits)
        {
            string fq = string.IsNullOrEmpty(s.Container) ? s.Name : $"{s.Container}.{s.Name}";
            Console.WriteLine($"  {KindName(s.Kind),-12} {fq}  {s.Path}:{s.Line}:{s.Col}");
        }
    }

    /// <summary>Print the symbol outline of one document (textDocument/documentSymbol), indented by nesting depth.</summary>
    /// <param name="file">Path to the .cs/.vb file (absolute, or relative to the workspace root).</param>
    [Command("symbols")]
    public async Task Symbols([Argument] string file, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var syms = await RoslynOps.DocumentSymbolsAsync(c, file, ct);
        if (syms.Count == 0) { Console.WriteLine($"no symbols in {file}"); return; }
        foreach (var s in syms)
            Console.WriteLine($"{new string(' ', s.Depth * 2)}{KindName(s.Kind),-12} {s.Name}  :{s.Line}:{s.Col}");
    }

    /// <summary>Rename the symbol at a 0-based file position solution-wide, writing the edits to disk.</summary>
    /// <param name="file">Path to the .cs/.vb file (absolute, or relative to the workspace root).</param>
    /// <param name="line">0-based line of the symbol occurrence.</param>
    /// <param name="col">0-based character/column of the symbol occurrence.</param>
    /// <param name="newName">The new identifier (no namespace).</param>
    [Command("rename")]
    public async Task Rename([Argument] string file, [Argument] int line, [Argument] int col, [Argument] string newName, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var changed = await RoslynOps.RenameAsync(c, file, line, col, newName, ct);
        Report(changed.Count == 0 ? $"no rename produced at {file}:{line}:{col}" : $"renamed → '{newName}'", changed);
    }

    /// <summary>Rename a type or namespace by fully-qualified name (e.g. 'My.Ns.OldClass') solution-wide, writing to disk.</summary>
    /// <param name="fullyQualifiedName">The qualified type/namespace name to rename.</param>
    /// <param name="newName">The new short identifier.</param>
    [Command("rename-name")]
    public async Task RenameName([Argument] string fullyQualifiedName, [Argument] string newName, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var loc = await RoslynOps.ResolveByNameAsync(c, fullyQualifiedName, ct);
        if (loc is null) { Console.Error.WriteLine($"could not resolve a type or namespace named '{fullyQualifiedName}'"); Environment.ExitCode = 1; return; }
        var changed = await RoslynOps.RenameAtAsync(c, loc.Value.uri, loc.Value.position, newName, ct);
        Report(changed.Count == 0 ? $"no rename produced for '{fullyQualifiedName}'" : $"renamed '{fullyQualifiedName}' → '{newName}'", changed);
    }

    /// <summary>Reformat a whole document with Roslyn's formatter and write it to disk.</summary>
    /// <param name="file">Path to the .cs/.vb file (absolute, or relative to the workspace root).</param>
    [Command("format")]
    public async Task Format([Argument] string file, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        bool changed = await RoslynOps.FormatAsync(c, file, ct);
        Console.WriteLine(changed ? $"formatted (written): {file}" : $"no formatting changes: {file}");
    }

    /// <summary>Connect to (or start) the shared Roslyn daemon for the current workspace — same derivation as the MCP.</summary>
    private static async Task<RoslynDaemonClient> Connect(CancellationToken ct)
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT") ?? Directory.GetCurrentDirectory();
        string? solution = Environment.GetEnvironmentVariable(SolutionLocator.OverrideEnvVar);
        // CLAUDE_PLUGIN_ROOT when launched by Claude; else derive from this assembly (cli/bin/Release/net8.0 → up 4).
        string pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        Action<string> log = s => Console.Error.WriteLine($"[crlsp] {s}");
        return await RoslynDaemonClient.ConnectAsync(root, solution, pluginRoot, log, ct)
            ?? throw new InvalidOperationException("could not reach or start the shared Roslyn daemon");
    }

    private static void Report(string headline, IReadOnlyList<string> changed)
    {
        Console.WriteLine(changed.Count == 0 ? headline : $"{headline} across {changed.Count} file(s):");
        foreach (string f in changed) Console.WriteLine($"  {f}");
    }

    /// <summary>LSP SymbolKind (1-26) → a short readable name.</summary>
    private static string KindName(int kind) => kind switch
    {
        1 => "file", 2 => "module", 3 => "namespace", 4 => "package", 5 => "class", 6 => "method",
        7 => "property", 8 => "field", 9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function",
        13 => "variable", 14 => "constant", 15 => "string", 16 => "number", 17 => "boolean", 18 => "array",
        19 => "object", 20 => "key", 21 => "null", 22 => "enum-member", 23 => "struct", 24 => "event",
        25 => "operator", 26 => "type-param", _ => $"kind{kind}",
    };
}
