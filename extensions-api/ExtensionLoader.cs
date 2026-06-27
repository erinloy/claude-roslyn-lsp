using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Extensions;

/// <summary>
/// Loads the extensions declared in <c>&lt;workspaceRoot&gt;/.claude-roslyn/extensions.json</c>. The manifest is a list of
/// entries; each names an assembly (path relative to the workspace root, or absolute), the <see cref="ICrlspExtension"/>
/// implementation type, an optional <c>config</c> object, and an <c>enabled</c> flag. Resolution is best-effort and never
/// throws into the host: a missing/broken extension is logged and skipped so the LSP keeps working.
///
/// <code>
/// {
///   "extensions": [
///     {
///       "name": "orders",
///       "assembly": "src/Orders.LspExtension/bin/Release/net8.0/Orders.LspExtension.dll",
///       "type": "Acme.Orders.LspExtension.OrdersExtension",
///       "enabled": true,
///       "config": { "endpoint": "http://localhost:8080" }
///     }
///   ]
/// }
/// </code>
/// </summary>
public static class ExtensionLoader
{
    public const string ManifestRelativePath = ".claude-roslyn/extensions.json";

    private static readonly ConcurrentDictionary<string, byte> _probeDirs = new(StringComparer.OrdinalIgnoreCase);
    private static int _resolverHooked;

    public static IReadOnlyList<LoadedExtension> Load(string workspaceRoot, string host, Action<string> log)
    {
        var result = new List<LoadedExtension>();
        string manifestPath = Path.Combine(workspaceRoot, ".claude-roslyn", "extensions.json");
        if (!File.Exists(manifestPath)) return result;

        log($"loading extensions from {manifestPath}");
        JsonNode? doc;
        try { doc = JsonNode.Parse(File.ReadAllText(manifestPath)); }
        catch (Exception ex) { log($"extensions.json parse error: {ex.Message}"); return result; }
        if (doc?["extensions"] is not JsonArray entries) { log("extensions.json has no 'extensions' array"); return result; }

        EnsureResolver();
        foreach (JsonNode? e in entries)
        {
            if (e is null) continue;
            string name = e["name"]?.GetValue<string>() ?? "(unnamed)";
            if (e["enabled"] is { } en && en.GetValue<bool>() == false) { log($"extension '{name}' disabled — skipping"); continue; }

            string? asmRel = e["assembly"]?.GetValue<string>();
            string? typeName = e["type"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(asmRel) || string.IsNullOrWhiteSpace(typeName))
            { log($"extension '{name}' missing 'assembly' or 'type' — skipping"); continue; }

            string asmPath = Path.IsPathRooted(asmRel) ? asmRel! : Path.GetFullPath(Path.Combine(workspaceRoot, asmRel!));
            if (!File.Exists(asmPath)) { log($"extension '{name}' assembly not found: {asmPath} — skipping (build it?)"); continue; }

            try
            {
                // Load from a shadow copy so the ORIGINAL dll is never locked — the author can rebuild it while this host
                // runs (the rebuilt tools take effect on the host's next start / MCP reconnect). The daemon goes further
                // and hot-reloads in place (see ReloadableExtensionHost); the MCP host registers tools once at startup, so
                // here shadow-copy is the win that matters: zero "stop the host to rebuild" friction.
                string loadPath = ShadowCopyForLoad(asmPath, name, host, log);
                _probeDirs.TryAdd(Path.GetDirectoryName(loadPath)!, 1); // so its dependencies resolve from beside it
                Assembly asm = Assembly.LoadFrom(loadPath);
                Type? type = asm.GetType(typeName!) ?? asm.GetTypes().FirstOrDefault(t => t.FullName == typeName);
                if (type is null) { log($"extension '{name}' type '{typeName}' not found in {Path.GetFileName(asmPath)} — skipping"); continue; }
                if (Activator.CreateInstance(type) is not ICrlspExtension ext)
                { log($"extension '{name}' type '{typeName}' is not an ICrlspExtension — skipping"); continue; }

                result.Add(new LoadedExtension(ext, asm, e["config"] as JsonObject, e["name"]?.GetValue<string>() ?? ext.Name));
                log($"extension '{name}' loaded ({type.FullName}) for host '{host}'");
            }
            catch (Exception ex) { log($"extension '{name}' failed to load: {ex.Message} — skipping"); }
        }
        return result;
    }

    // Copy the extension's whole output directory to a per-load shadow location and return the copied main dll, so the
    // original is never memory-mapped/locked by this host. Falls back to the original path if the copy fails.
    private static string ShadowCopyForLoad(string originalDll, string name, string host, Action<string> log)
    {
        try
        {
            string srcDir = Path.GetDirectoryName(originalDll)!;
            string dst = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "claude-roslyn-lsp", "ext-shadow", host, name, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(srcDir))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true); } catch { /* skip a locked sibling */ }
            }
            return Path.Combine(dst, Path.GetFileName(originalDll));
        }
        catch (Exception ex)
        {
            log($"extension '{name}' shadow-copy failed ({ex.Message}); loading in place — the original dll will be locked");
            return originalDll;
        }
    }

    // LoadFrom already probes an assembly's own directory; this also resolves dependencies that live alongside ANY loaded
    // extension (transitive deps the LoadFrom context misses), so an extension can ship its own dependency dlls.
    private static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverHooked, 1) == 1) return;
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            string simple = new AssemblyName(args.Name).Name + ".dll";
            foreach (string dir in _probeDirs.Keys)
            {
                string candidate = Path.Combine(dir, simple);
                if (File.Exists(candidate)) { try { return Assembly.LoadFrom(candidate); } catch { } }
            }
            return null;
        };
    }
}

/// <summary>An extension that was successfully instantiated, with its manifest metadata.</summary>
public sealed record LoadedExtension(ICrlspExtension Extension, Assembly Assembly, JsonObject? Config, string Name);
