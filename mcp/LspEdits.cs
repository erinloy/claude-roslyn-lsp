using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Mcp;

/// <summary>
/// Applies LSP edits (a <c>WorkspaceEdit</c> from rename, or a <c>TextEdit[]</c> from formatting) to files on disk. The
/// language server RETURNS edits; it does not write them — the agent works on files, so the MCP is what persists them.
/// LSP positions are 0-based (line, character-in-UTF-16-code-units), which maps directly onto a C# UTF-16 string.
/// </summary>
internal static class LspEdits
{
    public static string UriToPath(string uri)
    {
        try { return new Uri(uri).LocalPath; } catch { return uri; }
    }

    public static string PathToUri(string path)
    {
        try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; } catch { return path; }
    }

    /// <summary>Apply a WorkspaceEdit (either the <c>documentChanges</c> array or the <c>changes</c> map). Returns the
    /// distinct file paths that actually changed on disk.</summary>
    public static IReadOnlyList<string> ApplyWorkspaceEdit(JsonNode? workspaceEdit)
    {
        var changed = new List<string>();
        if (workspaceEdit is not JsonObject we) return changed;

        if (we["documentChanges"] is JsonArray docChanges)
        {
            foreach (var dc in docChanges)
            {
                string? uri = dc?["textDocument"]?["uri"]?.GetValue<string>();
                if (uri is null || dc?["edits"] is not JsonArray edits) continue;
                string path = UriToPath(uri);
                if (ApplyTextEdits(path, edits)) changed.Add(path);
            }
        }
        else if (we["changes"] is JsonObject changes)
        {
            foreach (var kv in changes)
            {
                if (kv.Value is not JsonArray edits) continue;
                string path = UriToPath(kv.Key);
                if (ApplyTextEdits(path, edits)) changed.Add(path);
            }
        }
        return changed.Distinct().ToList();
    }

    /// <summary>Apply a TextEdit[] to one file. Edits within a single array are non-overlapping (LSP guarantees it), so
    /// applying them highest-offset-first keeps earlier offsets valid. Returns true if the file content changed.</summary>
    public static bool ApplyTextEdits(string path, JsonArray edits)
    {
        if (!File.Exists(path) || edits.Count == 0) return false;
        string original = File.ReadAllText(path);
        int[] lineStarts = ComputeLineStarts(original);

        var spans = new List<(int start, int end, string text)>();
        foreach (var e in edits)
        {
            if (e?["range"] is not JsonObject range || e["newText"] is null) continue;
            int start = OffsetOf(range["start"], lineStarts, original.Length);
            int end = OffsetOf(range["end"], lineStarts, original.Length);
            if (start < 0 || end < 0 || end < start) continue;
            spans.Add((start, end, e["newText"]!.GetValue<string>()));
        }
        if (spans.Count == 0) return false;

        spans.Sort((a, b) => b.start.CompareTo(a.start)); // descending: edit the tail first
        var sb = new StringBuilder(original);
        foreach (var (start, end, text) in spans)
        {
            sb.Remove(start, end - start);
            sb.Insert(start, text);
        }
        string updated = sb.ToString();
        if (updated == original) return false;
        File.WriteAllText(path, updated);
        return true;
    }

    private static int OffsetOf(JsonNode? pos, int[] lineStarts, int textLen)
    {
        if (pos is null) return -1;
        int line = pos["line"]?.GetValue<int>() ?? -1;
        int ch = pos["character"]?.GetValue<int>() ?? -1;
        if (line < 0 || ch < 0 || line >= lineStarts.Length) return -1;
        return Math.Clamp(lineStarts[line] + ch, 0, textLen);
    }

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }
}
