namespace ClaudeRoslynLsp.Bridge;

/// <summary>What to hand the Roslyn server so it loads a workspace: a single solution, or a set of loose projects.</summary>
internal readonly record struct WorkspaceTarget(string? SolutionPath, IReadOnlyList<string> ProjectPaths)
{
    public bool HasAny => SolutionPath is not null || ProjectPaths.Count > 0;
}

/// <summary>
/// Decides which solution/projects to open for a workspace root. The Roslyn language server does NOT auto-discover a
/// solution (unlike OmniSharp/csharp-ls) — the client must tell it, via the <c>solution/open</c> or <c>project/open</c>
/// notification. This picks the target with explicit, predictable precedence so a multi-solution repo is deterministic.
/// </summary>
internal static class SolutionLocator
{
    /// <summary>
    /// Override env var: an explicit solution or project path (absolute, or relative to the workspace root). Set this
    /// in a multi-solution repo to pin the canonical solution (e.g. <c>CLAUDE_ROSLYN_SOLUTION=src/App.slnx</c>).
    /// </summary>
    public const string OverrideEnvVar = "CLAUDE_ROSLYN_SOLUTION";

    public static WorkspaceTarget Locate(string rootPath, string? overridePath, Action<string> log)
    {
        // 1. Explicit override wins — resolve relative to the root.
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            string resolved = Path.IsPathRooted(overridePath)
                ? overridePath
                : Path.GetFullPath(Path.Combine(rootPath, overridePath));
            if (File.Exists(resolved))
            {
                log($"workspace target from {OverrideEnvVar}: {resolved}");
                return IsProject(resolved)
                    ? new WorkspaceTarget(null, new[] { resolved })
                    : new WorkspaceTarget(resolved, Array.Empty<string>());
            }
            log($"{OverrideEnvVar}={overridePath} did not resolve to an existing file ({resolved}); falling back to discovery");
        }

        if (!Directory.Exists(rootPath))
            return new WorkspaceTarget(null, Array.Empty<string>());

        // 2. A solution AT the root. Prefer .slnx (the modern XML format) over .sln. A single match is unambiguous;
        //    if several exist we still pick deterministically (shortest name, then ordinal) and log it — the user can
        //    pin with the override if that guess is wrong.
        string? solution = PickSolution(rootPath);
        if (solution is not null)
        {
            log($"workspace solution: {solution}");
            return new WorkspaceTarget(solution, Array.Empty<string>());
        }

        // 3. A solution one level down (common: repo-root/src/Foo.slnx).
        foreach (var sub in SafeEnumerateDirectories(rootPath))
        {
            string? nested = PickSolution(sub);
            if (nested is not null)
            {
                log($"workspace solution (nested): {nested}");
                return new WorkspaceTarget(nested, Array.Empty<string>());
            }
        }

        // 4. No solution — open the loose projects (bounded scan so a huge tree can't stall startup).
        var projects = FindProjects(rootPath, maxResults: 64).ToList();
        if (projects.Count > 0)
        {
            log($"no solution found; opening {projects.Count} project(s)");
            return new WorkspaceTarget(null, projects);
        }

        log("no .slnx/.sln/.csproj/.vbproj found under the workspace root");
        return new WorkspaceTarget(null, Array.Empty<string>());
    }

    private static string? PickSolution(string dir)
    {
        var slnx = SafeEnumerateFiles(dir, "*.slnx").OrderBy(NameLen).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        if (slnx is not null) return slnx;
        return SafeEnumerateFiles(dir, "*.sln").OrderBy(NameLen).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    }

    private static IEnumerable<string> FindProjects(string root, int maxResults)
    {
        int count = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && count < maxResults)
        {
            string dir = stack.Pop();
            foreach (var proj in SafeEnumerateFiles(dir, "*.csproj").Concat(SafeEnumerateFiles(dir, "*.vbproj")))
            {
                yield return proj;
                if (++count >= maxResults) yield break;
            }
            foreach (var sub in SafeEnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name is "bin" or "obj" or "node_modules" or ".git" or ".vs") continue;
                stack.Push(sub);
            }
        }
    }

    private static bool IsProject(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);

    private static int NameLen(string p) => Path.GetFileName(p).Length;

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }
}
