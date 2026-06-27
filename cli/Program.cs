using System.Text.Json.Nodes;
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

    /// <summary>Pull diagnostics (errors/warnings) for a document — what the LSP edit-feedback shows.</summary>
    /// <param name="file">Path to the .cs/.vb file (absolute, or relative to the workspace root).</param>
    [Command("diag")]
    public async Task Diag([Argument] string file, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var diags = await RoslynOps.DiagnosticsAsync(c, file, ct);
        if (diags.Count == 0) { Console.WriteLine($"no diagnostics: {file}"); return; }
        Console.WriteLine($"diagnostics ({diags.Count}):");
        foreach (var d in diags)
            Console.WriteLine($"  {SevName(d.Severity),-7} :{d.Line}:{d.Col} {d.Code}: {d.Message}");
    }

    /// <summary>Go to the definition of the symbol at a 0-based file position.</summary>
    [Command("def")]
    public async Task Def([Argument] string file, [Argument] int line, [Argument] int col, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        PrintLocs("definition", await RoslynOps.DefinitionAsync(c, file, line, col, ct), file, line, col);
    }

    /// <summary>Find implementations of the interface/abstract/virtual member at a 0-based file position.</summary>
    [Command("impl")]
    public async Task Impl([Argument] string file, [Argument] int line, [Argument] int col, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        PrintLocs("implementations", await RoslynOps.ImplementationsAsync(c, file, line, col, ct), file, line, col);
    }

    /// <summary>Jump to the declared type of the symbol at a 0-based file position.</summary>
    [Command("typedef")]
    public async Task TypeDef([Argument] string file, [Argument] int line, [Argument] int col, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        PrintLocs("type definition", await RoslynOps.TypeDefinitionAsync(c, file, line, col, ct), file, line, col);
    }

    /// <summary>Hover info (type signature + XML doc) for the symbol at a 0-based file position.</summary>
    [Command("hover")]
    public async Task Hover([Argument] string file, [Argument] int line, [Argument] int col, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        string? h = await RoslynOps.HoverAsync(c, file, line, col, ct);
        Console.WriteLine(string.IsNullOrWhiteSpace(h) ? $"no hover info at {file}:{line}:{col}" : h);
    }

    /// <summary>List the code actions (quick fixes + refactorings) available at a 0-based file position.</summary>
    [Command("actions")]
    public async Task Actions([Argument] string file, [Argument] int line, [Argument] int col, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var acts = await RoslynOps.CodeActionsAsync(c, file, line, col, null, null, ct);
        if (acts.Count == 0) { Console.WriteLine($"no code actions at {file}:{line}:{col}"); return; }
        Console.WriteLine($"code actions ({acts.Count}) — apply with: crlsp fix <file> <line> <col> \"<title>\"");
        foreach (var a in acts) Console.WriteLine($"  [{(string.IsNullOrEmpty(a.Kind) ? "action" : a.Kind)}] {a.Title}");
    }

    /// <summary>Apply the code action whose title matches at a 0-based file position; writes the edit to disk.</summary>
    [Command("fix")]
    public async Task Fix([Argument] string file, [Argument] int line, [Argument] int col, [Argument] string title, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var changed = await RoslynOps.ApplyCodeActionAsync(c, file, line, col, title, null, null, ct);
        if (changed.Count == 0) { Console.WriteLine($"no code action titled '{title}' applied at {file}:{line}:{col}"); return; }
        Console.WriteLine($"applied '{title}' across {changed.Count} file(s):");
        foreach (var f in changed) Console.WriteLine($"  {f}");
    }

    /// <summary>Organize a C# file's usings (remove unnecessary + sort); writes to disk.</summary>
    [Command("organize")]
    public async Task Organize([Argument] string file, CancellationToken ct)
    {
        await using var c = await Connect(ct);
        var changed = await RoslynOps.OrganizeImportsAsync(c, file, ct);
        if (changed.Count == 0) { Console.WriteLine($"usings already organized: {file}"); return; }
        Console.WriteLine($"organized usings across {changed.Count} file(s):");
        foreach (var f in changed) Console.WriteLine($"  {f}");
    }

    /// <summary>Probe the PUSH path: open a file, then print the publishDiagnostics the daemon broadcasts back (the same
    /// notification Claude Code consumes for edit-feedback). With --apply, mid-stream it writes another file's content to
    /// disk and sends didChange — exercising the daemon's disk-resync edit path. Verifies the pull→push diagnostics bridge.</summary>
    /// <param name="file">Path to the .cs/.vb file.</param>
    /// <param name="seconds">How long to watch for broadcasts before printing the latest.</param>
    /// <param name="apply">Optional path whose content is written over <paramref name="file"/> mid-watch (then didChange), to test edit feedback.</param>
    [Command("watchdiag")]
    public async Task WatchDiag([Argument] string file, int seconds = 8, string? apply = null, CancellationToken ct = default)
    {
        string uri = LspEdits.PathToUri(file);
        int rounds = 0;
        List<string> latest = new();
        void OnNote(JsonNode n)
        {
            if (n["method"]?.GetValue<string>() != "textDocument/publishDiagnostics") return;
            string pushUri = n["params"]?["uri"]?.GetValue<string>() ?? "";
            bool isOwn = string.Equals(pushUri, uri, StringComparison.OrdinalIgnoreCase);
            // Print EVERY publishDiagnostics, labeled by file — so this probe shows per-client routing: a file we DID open
            // arrives full; a file another client opened arrives here errors-only (general visibility, no hint flood).
            var lines = new List<string>();
            foreach (JsonNode? d in n["params"]?["diagnostics"]?.AsArray() ?? new JsonArray())
            {
                JsonNode? start = d?["range"]?["start"];
                lines.Add($"  {SevName(d?["severity"]?.GetValue<int>() ?? 0),-7} :{start?["line"]?.GetValue<int>()}:{start?["character"]?.GetValue<int>()} {d?["code"]}: {d?["message"]?.GetValue<string>()}");
            }
            rounds++;
            if (isOwn) latest = lines;
            string label = isOwn ? "[own]" : $"[other:{System.IO.Path.GetFileName(new Uri(pushUri).LocalPath)}]";
            Console.WriteLine($"[push #{rounds}] {label} {(lines.Count == 0 ? "0 diagnostics" : $"{lines.Count} diagnostic(s)")}");
            lines.ForEach(Console.WriteLine);
        }

        string root = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT") ?? Directory.GetCurrentDirectory();
        string? solution = Environment.GetEnvironmentVariable(SolutionLocator.OverrideEnvVar);
        string pluginRoot = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        Action<string> log = s => Console.Error.WriteLine($"[crlsp] {s}");

        await using var c = await RoslynDaemonClient.ConnectAsync(root, solution, pluginRoot, log, ct, OnNote)
            ?? throw new InvalidOperationException("could not reach or start the shared Roslyn daemon");

        // Open the doc — the daemon's bridge pulls diagnostics and broadcasts publishDiagnostics back to us.
        string text = File.Exists(file) ? File.ReadAllText(file) : "";
        string langId = file.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ? "vb" : "csharp";
        c.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = langId, ["version"] = 1, ["text"] = text },
        });

        // Optionally simulate an edit: write new content to disk, then didChange (the daemon re-reads disk, not our buffer).
        if (apply is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(4, seconds / 2)), ct).ConfigureAwait(false);
            File.WriteAllText(file, File.ReadAllText(apply));
            Console.WriteLine($"[edit] wrote {apply} → {file}, sending didChange");
            c.Notify("textDocument/didChange", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = File.ReadAllText(file) }),
            });
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        c.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });

        if (rounds == 0) Console.WriteLine($"NO publishDiagnostics within {seconds}s: {file}");
        else Console.WriteLine($"final: {latest.Count} diagnostic(s) after {rounds} push(es)");
    }

    /// <summary>LSP DiagnosticSeverity (1-4) → name.</summary>
    private static string SevName(int s) => s switch
    {
        1 => "error", 2 => "warning", 3 => "info", 4 => "hint", _ => $"sev{s}",
    };

    private static void PrintLocs(string label, IReadOnlyList<RoslynOps.RefLoc> locs, string file, int line, int col)
    {
        if (locs.Count == 0) { Console.WriteLine($"no {label} at {file}:{line}:{col}"); return; }
        Console.WriteLine($"{label} ({locs.Count}):");
        foreach (var r in locs) Console.WriteLine($"  {r.Path}:{r.Line}:{r.Col}");
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
