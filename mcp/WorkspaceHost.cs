using ClaudeRoslynLsp.Bridge; // SolutionLocator (linked)
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// Owns the Roslyn <see cref="Solution"/> the refactoring tools operate on. Loading a solution via MSBuild is expensive,
/// so it's done once (lazily) and kept warm; <see cref="ReloadAsync"/> re-opens it after the agent (or a tool) has
/// written changes to disk so subsequent operations see current text.
///
/// In a large monorepo do NOT point this at the 300-project umbrella solution — set <c>CLAUDE_ROSLYN_SOLUTION</c> to the
/// subsystem <c>.slnx</c> you're working in (same env var the LSP bridge uses), so only that graph is loaded.
/// </summary>
public sealed class WorkspaceHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _log = s => Console.Error.WriteLine($"[mcp] {s}");
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;

    public async Task<Solution> GetSolutionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _solution ??= await LoadAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Re-open the workspace from disk (call after writing edits so the warm solution isn't stale).</summary>
    public async Task<Solution> ReloadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _workspace?.Dispose();
            _workspace = null;
            _solution = null;
            return _solution = await LoadAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<Solution> LoadAsync(CancellationToken ct)
    {
        var ws = MSBuildWorkspace.Create();
        ws.WorkspaceFailed += (_, e) =>
        {
            // Project-load diagnostics are common and non-fatal (missing optional targets etc.) — log, don't throw.
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) _log($"workspace: {e.Diagnostic.Message}");
        };
        _workspace = ws;

        string root = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT") ?? Directory.GetCurrentDirectory();
        string? overridePath = Environment.GetEnvironmentVariable(SolutionLocator.OverrideEnvVar);
        WorkspaceTarget target = SolutionLocator.Locate(root, overridePath, _log);

        if (target.SolutionPath is not null
            && target.SolutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            // MSBuildWorkspace's solution parser only reads the legacy .sln text format — it can't parse the modern XML
            // .slnx. Extract the project list from the .slnx ourselves and open the projects (P2P refs pull in the rest).
            var projects = ReadSlnxProjects(target.SolutionPath, _log);
            _log($"opening {projects.Count} project(s) from {Path.GetFileName(target.SolutionPath)} …");
            foreach (var proj in projects)
                await ws.OpenProjectAsync(proj, cancellationToken: ct).ConfigureAwait(false);
        }
        else if (target.SolutionPath is not null)
        {
            _log($"opening solution {target.SolutionPath} …");
            await ws.OpenSolutionAsync(target.SolutionPath, cancellationToken: ct).ConfigureAwait(false);
        }
        else if (target.ProjectPaths.Count > 0)
        {
            _log($"opening {target.ProjectPaths.Count} project(s) …");
            foreach (var proj in target.ProjectPaths)
                await ws.OpenProjectAsync(proj, cancellationToken: ct).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                $"no solution/project found under {root}. Set {SolutionLocator.OverrideEnvVar} to a .slnx/.sln/.csproj path.");
        }

        _log($"workspace ready: {ws.CurrentSolution.Projects.Count()} project(s)");
        return ws.CurrentSolution;
    }

    /// <summary>Find the loaded <see cref="Document"/> whose file path matches <paramref name="filePath"/>.</summary>
    public static Document? FindDocument(Solution solution, string filePath)
    {
        string target = Normalize(filePath);
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath is not null && Normalize(d.FilePath) == target);
    }

    /// <summary>Position of a 0-based (line, character) in a document's text — the LSP coordinate convention.</summary>
    public static int PositionOf(SourceText text, int line, int character)
    {
        if (line < 0 || line >= text.Lines.Count) throw new ArgumentOutOfRangeException(nameof(line));
        TextLine textLine = text.Lines[line];
        int pos = textLine.Start + character;
        return Math.Clamp(pos, textLine.Start, textLine.End);
    }

    /// <summary>
    /// Write every document that differs between <paramref name="original"/> and <paramref name="updated"/> back to disk.
    /// Returns the changed file paths. This is how a Roslyn refactoring (which produces a new in-memory Solution) becomes
    /// edits the agent can see — the agent works on files, not on our workspace.
    /// </summary>
    public static IReadOnlyList<string> WriteChangedDocuments(Solution original, Solution updated, CancellationToken ct)
    {
        var changed = new List<string>();
        foreach (var projectChange in updated.GetChanges(original).GetProjectChanges())
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                Document newDoc = updated.GetDocument(docId)!;
                if (newDoc.FilePath is null) continue;
                SourceText newText = newDoc.GetTextAsync(ct).GetAwaiter().GetResult();
                File.WriteAllText(newDoc.FilePath, newText.ToString());
                changed.Add(newDoc.FilePath);
            }
        }
        return changed;
    }

    /// <summary>
    /// Read the project paths out of a <c>.slnx</c> (the modern XML solution format). The schema is simple: any number of
    /// <c>&lt;Project Path="…"/&gt;</c> elements, optionally nested in <c>&lt;Folder&gt;</c>s, with paths relative to the
    /// .slnx's directory. We only need the C#/VB projects Roslyn can open.
    /// </summary>
    private static IReadOnlyList<string> ReadSlnxProjects(string slnxPath, Action<string> log)
    {
        var result = new List<string>();
        try
        {
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(slnxPath))!;
            System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(slnxPath);
            foreach (var projEl in doc.Descendants("Project"))
            {
                string? rel = projEl.Attribute("Path")?.Value;
                if (string.IsNullOrWhiteSpace(rel)) continue;
                if (!rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                    !rel.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)) continue;
                string full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('\\', Path.DirectorySeparatorChar)));
                if (File.Exists(full)) result.Add(full);
                else log($"slnx project not found on disk, skipping: {rel}");
            }
        }
        catch (Exception ex) { log($"failed to read .slnx '{slnxPath}': {ex.Message}"); }
        return result;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/'); }
        catch { return path.Replace('\\', '/').TrimEnd('/'); }
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        return ValueTask.CompletedTask;
    }
}
