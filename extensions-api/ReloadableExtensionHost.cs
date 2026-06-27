using System.Reflection;
using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Extensions;

/// <summary>
/// Loads the manifest's extensions and keeps them HOT-RELOADABLE: each extension lives in its own collectible
/// <see cref="ExtensionLoadContext"/>, loaded from a shadow copy (so the original dll is never locked and the author can
/// rebuild it freely), and a file-watch on each original dll swaps in the rebuilt version at runtime — no host restart.
///
/// Why this shape:
/// <list type="bullet">
/// <item>SHADOW COPY — the host loads <c>&lt;localappdata&gt;/.../ext-shadow/&lt;name&gt;/&lt;n&gt;/X.dll</c>, never the manifest path,
///   so the author rebuilds the real dll while the host runs.</item>
/// <item>COLLECTIBLE ALC per extension — a reload unloads the old context and loads the new dll. The FUNCTIONAL guarantee
///   (new code runs) needs only the new context; the old context unloading is best-effort cleanup, so a stray reference
///   degrades to a small leak, never a failure to reload.</item>
/// <item>RESILIENT — a reload that throws (mid-write file, bad build, init failure) keeps the OLD instance serving and logs;
///   the running system never goes dark because a rebuild was in flight.</item>
/// </list>
/// Capability views (<see cref="DiagnosticExtensions"/> etc.) return the CURRENT instances and must be read live (not
/// cached) by the host so a reload takes effect on the next request.
/// </summary>
public sealed class ReloadableExtensionHost : IAsyncDisposable
{
    private readonly string _hostName;
    private readonly Action<string> _log;
    private readonly Func<string, JsonObject?, ExtensionContext> _contextFactory;
    private readonly Action _onReloaded;
    private readonly CancellationToken _ct;
    private readonly string _shadowRoot;
    private readonly List<Slot> _slots = new();
    private readonly object _gate = new();
    private int _shadowSeq;

    private volatile IReadOnlyList<IDiagnosticExtension> _diag = Array.Empty<IDiagnosticExtension>();
    private volatile IReadOnlyList<IHoverExtension> _hover = Array.Empty<IHoverExtension>();
    private volatile IReadOnlyList<ISymbolExtension> _symbol = Array.Empty<ISymbolExtension>();
    private volatile IReadOnlyList<ICrlspExtension> _all = Array.Empty<ICrlspExtension>();

    public IReadOnlyList<IDiagnosticExtension> DiagnosticExtensions => _diag;
    public IReadOnlyList<IHoverExtension> HoverExtensions => _hover;
    public IReadOnlyList<ISymbolExtension> SymbolExtensions => _symbol;
    public IReadOnlyList<ICrlspExtension> Extensions => _all;

    private ReloadableExtensionHost(string hostName, Action<string> log,
        Func<string, JsonObject?, ExtensionContext> contextFactory, Action onReloaded, CancellationToken ct)
    {
        _hostName = hostName; _log = log; _contextFactory = contextFactory; _onReloaded = onReloaded; _ct = ct;
        _shadowRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "claude-roslyn-lsp", "ext-shadow", hostName);
    }

    /// <summary>Load every enabled manifest extension, initialize it, and start watching for rebuilds.</summary>
    public static async Task<ReloadableExtensionHost> StartAsync(string workspaceRoot, string hostName, Action<string> log,
        Func<string, JsonObject?, ExtensionContext> contextFactory, Action onReloaded, CancellationToken ct)
    {
        var host = new ReloadableExtensionHost(hostName, log, contextFactory, onReloaded, ct);
        string manifestPath = Path.Combine(workspaceRoot, ".claude-roslyn", "extensions.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                JsonNode? doc = JsonNode.Parse(File.ReadAllText(manifestPath));
                if (doc?["extensions"] is JsonArray entries)
                {
                    log($"loading extensions from {manifestPath} (hot-reload enabled)");
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

                        var slot = new Slot { Name = name, OriginalPath = asmPath, TypeName = typeName!, Config = e["config"] as JsonObject };
                        host._slots.Add(slot);
                        await host.LoadIntoSlotAsync(slot, initial: true).ConfigureAwait(false);
                        host.StartWatching(slot);
                    }
                }
                else log("extensions.json has no 'extensions' array");
            }
            catch (Exception ex) { log($"extensions.json load error: {ex.Message}"); }
        }
        host.RebuildSnapshots();
        return host;
    }

    /// <summary>(Re)load one slot from its current original dll: shadow-copy → new collectible ALC → instantiate → init.
    /// On any failure the slot keeps its previous instance (caller logged); returns true only on a successful swap.</summary>
    private async Task<bool> LoadIntoSlotAsync(Slot slot, bool initial)
    {
        ExtensionLoadContext? newAlc = null;
        try
        {
            string shadowDir = ShadowCopy(slot.OriginalPath, slot.Name);
            string mainDll = Path.Combine(shadowDir, Path.GetFileName(slot.OriginalPath));
            newAlc = new ExtensionLoadContext(mainDll, $"crlsp-ext-{slot.Name}-{Interlocked.Increment(ref _shadowSeq)}");
            Assembly asm = newAlc.LoadFromAssemblyPath(mainDll);
            Type? type = asm.GetType(slot.TypeName) ?? asm.GetTypes().FirstOrDefault(t => t.FullName == slot.TypeName);
            if (type is null) { _log($"extension '{slot.Name}' type '{slot.TypeName}' not found — keeping previous"); newAlc.Unload(); return false; }
            if (Activator.CreateInstance(type) is not ICrlspExtension ext)
            { _log($"extension '{slot.Name}' is not an ICrlspExtension — keeping previous"); newAlc.Unload(); return false; }

            await ext.InitializeAsync(_contextFactory(slot.Name, slot.Config), _ct).ConfigureAwait(false);

            // Swap in only after a clean init, so a failed reload never replaces a working instance.
            ICrlspExtension? oldExt; ExtensionLoadContext? oldAlc;
            lock (_gate) { oldExt = slot.Ext; oldAlc = slot.Alc; slot.Ext = ext; slot.Alc = newAlc; }
            RebuildSnapshots();
            _log($"extension '{slot.Name}' {(initial ? "loaded" : "RELOADED")} ({type.FullName}) for host '{_hostName}'");

            if (!initial && oldExt is not null) { try { await oldExt.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _log($"extension '{slot.Name}' old-dispose error: {ex.Message}"); } }
            oldAlc?.Unload(); // best-effort retire of the previous context (GC completes the unload)
            return true;
        }
        catch (Exception ex)
        {
            _log($"extension '{slot.Name}' {(initial ? "load" : "reload")} failed: {ex.Message} — keeping previous");
            try { newAlc?.Unload(); } catch { }
            return false;
        }
    }

    private string ShadowCopy(string originalDll, string name)
    {
        string srcDir = Path.GetDirectoryName(originalDll)!;
        string dst = Path.Combine(_shadowRoot, name, Interlocked.Increment(ref _shadowSeq).ToString());
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(srcDir))
        {
            try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true); } catch { /* skip a locked sibling */ }
        }
        return dst;
    }

    private void StartWatching(Slot slot)
    {
        try
        {
            var fsw = new FileSystemWatcher(Path.GetDirectoryName(slot.OriginalPath)!, Path.GetFileName(slot.OriginalPath))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime };
            // A build writes the dll in several bursts; debounce so we reload once, after it settles.
            slot.Debounce = new Timer(_ => _ = ReloadDebouncedAsync(slot), null, Timeout.Infinite, Timeout.Infinite);
            void Bump(object? _, FileSystemEventArgs __) { try { slot.Debounce!.Change(800, Timeout.Infinite); } catch { } }
            fsw.Changed += Bump; fsw.Created += Bump; fsw.Renamed += (s, e) => Bump(s, e);
            fsw.EnableRaisingEvents = true;
            slot.Watcher = fsw;
            _log($"extension '{slot.Name}' watching {slot.OriginalPath} for rebuilds (hot-reload)");
        }
        catch (Exception ex) { _log($"extension '{slot.Name}' watch setup failed (no hot-reload): {ex.Message}"); }
    }

    private async Task ReloadDebouncedAsync(Slot slot)
    {
        if (Interlocked.CompareExchange(ref slot.Reloading, 1, 0) == 1) return; // a reload is already running; the watcher will re-fire
        try
        {
            if (!await WaitReadableAsync(slot.OriginalPath).ConfigureAwait(false))
            { _log($"extension '{slot.Name}' dll not readable after rebuild — skipping reload"); return; }
            bool ok = await LoadIntoSlotAsync(slot, initial: false).ConfigureAwait(false);
            if (ok) { try { _onReloaded(); } catch (Exception ex) { _log($"onReloaded handler threw: {ex.Message}"); } }
        }
        catch (Exception ex) { _log($"extension '{slot.Name}' reload error: {ex.Message}"); }
        finally { Interlocked.Exchange(ref slot.Reloading, 0); }
    }

    /// <summary>Wait until the rebuilt dll can be opened (the build has finished writing it).</summary>
    private static async Task<bool> WaitReadableAsync(string path)
    {
        for (int i = 0; i < 20; i++)
        {
            try { using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return s.Length > 0; }
            catch { await Task.Delay(150).ConfigureAwait(false); }
        }
        return false;
    }

    private void RebuildSnapshots()
    {
        lock (_gate)
        {
            var all = _slots.Select(s => s.Ext).Where(e => e is not null).Cast<ICrlspExtension>().ToArray();
            _all = all;
            _diag = all.OfType<IDiagnosticExtension>().ToArray();
            _hover = all.OfType<IHoverExtension>().ToArray();
            _symbol = all.OfType<ISymbolExtension>().ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Slot s in _slots)
        {
            try { s.Watcher?.Dispose(); } catch { }
            try { s.Debounce?.Dispose(); } catch { }
            if (s.Ext is not null) { try { await s.Ext.DisposeAsync().ConfigureAwait(false); } catch { } }
            try { s.Alc?.Unload(); } catch { }
        }
    }

    private sealed class Slot
    {
        public required string Name;
        public required string OriginalPath;
        public required string TypeName;
        public required JsonObject? Config;
        public ICrlspExtension? Ext;
        public ExtensionLoadContext? Alc;
        public FileSystemWatcher? Watcher;
        public Timer? Debounce;
        public int Reloading; // 0/1 guard so overlapping watcher events don't reload concurrently
    }
}
