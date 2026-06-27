using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeRoslynLsp.Bridge; // JsonRpc + SolutionLocator (linked)
using Sluice;                 // IFrameChannel — the client↔daemon transport seam

namespace ClaudeRoslynLsp.Daemon;

/// <summary>
/// Fans MANY client LSP sessions onto ONE Roslyn language server — the centralization core. The server's workspace
/// (compilations, syntax trees: the real memory cost) is loaded once and shared, so N Claude instances cost 1× not N×.
///
/// How the multiplex is kept safe:
///  - The daemon sends the single <c>initialize</c>/<c>initialized</c>/<c>solution/open</c> at startup and caches the
///    server's initialize RESULT. Each client's <c>initialize</c> is answered from that cache — never re-sent.
///  - Client request ids are namespaced to globally-unique ids before forwarding; responses route back by that id.
///  - Server→client requests (workspace/configuration, registerCapability, *progress/create, *refresh) are answered by
///    the daemon itself, not forwarded — clients never see mid-session server requests.
///  - Per-client document sync: didOpen/didClose are ref-counted (forwarded once / closed when the last client drops);
///    didChange/didSave are dropped — Claude edits on DISK and the server file-watches, so disk is the source of truth.
///    This is exactly why read-only sharing is safe.
/// </summary>
internal sealed class LspMultiplexer
{
    private readonly LspMessageWriter _lsWriter;
    private readonly LspMessageReader _lsReader;
    private readonly Action<string> _log;
    private readonly CancellationToken _ct;

    private readonly TaskCompletionSource<JsonObject> _initResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, (ClientSession session, JsonNode? originalId)> _pending = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _internalPending = new(); // daemon's own LS requests (diagnostic pulls)
    private readonly ConcurrentDictionary<int, ClientSession> _clients = new();
    private readonly ConcurrentDictionary<string, int> _openDocs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _docVersions = new(StringComparer.Ordinal);          // monotonic didChange version per open doc
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _diagDebounce = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _lastErrorSig = new(StringComparer.Ordinal);            // last errors-only signature published to non-openers, per uri
    private long _nextGlobalId = 1; // 0 reserved for the daemon's own initialize
    private int _nextClientId = 0;

    // Accessors (not fixed lists) so a hot-reload of an extension takes effect on the next request — the daemon reads the
    // CURRENT instances from the reloadable host each time, never a cached snapshot.
    private readonly Func<IReadOnlyList<IDiagnosticExtension>> _diagExtensions;
    private readonly Func<IReadOnlyList<IHoverExtension>> _hoverExtensions;
    private readonly Func<IReadOnlyList<ISymbolExtension>> _symbolExtensions;

    public LspMultiplexer(Stream lsIn, Stream lsOut, Action<string> log, CancellationToken ct,
        Func<IReadOnlyList<IDiagnosticExtension>>? diagExtensions = null, Func<IReadOnlyList<IHoverExtension>>? hoverExtensions = null,
        Func<IReadOnlyList<ISymbolExtension>>? symbolExtensions = null)
    {
        _lsWriter = new LspMessageWriter(lsIn);
        _lsReader = new LspMessageReader(lsOut);
        _log = log;
        _ct = ct;
        _diagExtensions = diagExtensions ?? (static () => Array.Empty<IDiagnosticExtension>());
        _hoverExtensions = hoverExtensions ?? (static () => Array.Empty<IHoverExtension>());
        _symbolExtensions = symbolExtensions ?? (static () => Array.Empty<ISymbolExtension>());
    }

    /// <summary>STREAMS push primitive handed to extensions: re-pull + re-publish a document's diagnostics (now carrying
    /// the extension's updated live state) through per-client routing. <paramref name="uri"/> null ⇒ every open document.</summary>
    public void RefreshDiagnostics(string? uri)
    {
        if (uri is not null) { TriggerDiagnostics(uri, resyncFromDisk: false); return; }
        foreach (string open in _openDocs.Keys) TriggerDiagnostics(open, resyncFromDisk: false);
    }

    public int ClientCount => _clients.Count;
    public event Action? ClientCountChanged;

    /// <summary>Boot the single LS: pump its output, send initialize, capture caps, open the workspace.</summary>
    public async Task StartAsync(string rootPath, string? solutionOverride)
    {
        _ = Task.Run(() => PumpServerAsync());

        await _lsWriter.WriteJsonAsync(BuildInitialize(rootPath), _ct).ConfigureAwait(false);
        await _initResult.Task.ConfigureAwait(false); // wait for the server's initialize result (caps cached)
        await _lsWriter.WriteJsonAsync(Notify("initialized", new JsonObject()), _ct).ConfigureAwait(false);
        await OpenWorkspaceAsync(rootPath, solutionOverride).ConfigureAwait(false);
        _log("LS initialized + workspace opened — daemon ready for clients");
    }

    public void AddClient(IFrameChannel channel)
    {
        int id = Interlocked.Increment(ref _nextClientId);
        var session = new ClientSession(id, channel, this, _log);
        _clients[id] = session;
        _log($"client {id} connected ({_clients.Count} active)");
        ClientCountChanged?.Invoke();
        // Announce our PID so the client can watch us die — the symmetric peer-death signal shared memory lacks. Without
        // this, a daemon restart leaves the client blocked forever on the dead ring (orphaning in-flight requests).
        try { channel.WriteFrame(Encoding.UTF8.GetBytes($"{{\"{PipeKey.DaemonPidKey}\":{Environment.ProcessId}}}")); }
        catch (Exception ex) { _log($"daemon-pid hello failed for client {id}: {ex.Message}"); }
        session.Start(_ct);
    }

    private void RemoveClient(ClientSession session)
    {
        if (_clients.TryRemove(session.Id, out _))
        {
            _log($"client {session.Id} disconnected ({_clients.Count} active)");
            // Drop any pending requests owned by this session so the maps don't leak.
            foreach (var kv in _pending)
                if (ReferenceEquals(kv.Value.session, session)) _pending.TryRemove(kv.Key, out _);
            // Release the docs this session held open. A client can vanish without sending didClose (PID-watch close),
            // which would otherwise leave the doc open in Roslyn forever; synthesize a didClose per uri to decrement the
            // shared ref-count (and tell the server to close it when this was the last opener).
            foreach (string uri in session.TakeOpenUris()) _ = ReleaseOpenDocAsync(uri);
            ClientCountChanged?.Invoke();
        }
    }

    // ---- server → (daemon) ----------------------------------------------------------------------------------------

    private async Task PumpServerAsync()
    {
        try
        {
            while (!_ct.IsCancellationRequested)
            {
                LspMessage? msg = await _lsReader.ReadAsync(_ct).ConfigureAwait(false);
                if (msg is null) { _log("LS closed its output — daemon will exit"); break; }
                JsonNode? json = msg.Json;
                if (json is null) continue;

                string? method = json["method"]?.GetValue<string>();
                JsonNode? idNode = json["id"];

                if (method is null && idNode is not null)
                {
                    // Response from the LS → route to the owning client (or capture the daemon's init result).
                    long? gid = ParseId(idNode);
                    if (gid == 0) { _initResult.TrySetResult(json["result"]?.AsObject() ?? new JsonObject()); continue; }
                    if (gid is long ig && _internalPending.TryRemove(ig, out var itcs)) { itcs.TrySetResult(json["result"]); continue; }
                    if (gid is long g && _pending.TryRemove(g, out var p))
                    {
                        JsonObject clone = json.DeepClone()!.AsObject();
                        clone["id"] = p.originalId?.DeepClone();
                        await p.session.SendAsync(clone).ConfigureAwait(false);
                    }
                }
                else if (method is not null && idNode is not null)
                {
                    await AnswerServerRequestAsync(idNode, method, json).ConfigureAwait(false);
                }
                else if (method is not null)
                {
                    await BroadcastAsync(msg).ConfigureAwait(false); // diagnostics / progress / log
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"server pump error: {ex.Message}"); }
    }

    /// <summary>Answer the requests a real client must answer, so the LS never blocks waiting on us.</summary>
    private async Task AnswerServerRequestAsync(JsonNode idNode, string method, JsonNode request)
    {
        JsonNode? result = null;
        if (method == "workspace/configuration")
        {
            int n = request["params"]?["items"]?.AsArray()?.Count ?? 0;
            var arr = new JsonArray();
            for (int i = 0; i < n; i++) arr.Add(null);
            result = arr;
        }
        // registerCapability / unregisterCapability / workDoneProgress/create / *refresh / default → null result.
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result };
        await _lsWriter.WriteJsonAsync(response, _ct).ConfigureAwait(false);

        // Roslyn computes diagnostics asynchronously and signals "they changed, re-pull" via this refresh request. That
        // is our cue to re-pull every open doc and push fresh publishDiagnostics — the event that makes edit-feedback land.
        if (method == "workspace/diagnostic/refresh")
            foreach (string uri in _openDocs.Keys) TriggerDiagnostics(uri, resyncFromDisk: false);
    }

    private async Task BroadcastAsync(LspMessage msg)
    {
        foreach (var c in _clients.Values)
        {
            try { await c.SendRawAsync(msg.Raw).ConfigureAwait(false); }
            catch { /* a dead client gets reaped by its own read loop */ }
        }
    }

    // ---- client → (daemon) ----------------------------------------------------------------------------------------

    public async Task HandleClientMessageAsync(ClientSession session, LspMessage msg)
    {
        JsonNode? json = msg.Json;
        if (json is null) return;
        string? method = json["method"]?.GetValue<string>();
        JsonNode? idNode = json["id"];

        // Hover augmentation: only when an extension contributes hover. Otherwise hover takes the unchanged generic
        // forward path below (zero behaviour change for the common case / a daemon with no hover extensions).
        if (_hoverExtensions().Count > 0 && method == "textDocument/hover" && idNode is not null)
        {
            _ = HandleHoverAsync(session, idNode.DeepClone(), json.DeepClone()!.AsObject());
            return;
        }

        // workspaceSymbol augmentation (SYMBOLS): same guard/shape as hover — merge the running system's catalog into
        // Roslyn's results when an extension contributes symbols; otherwise the unchanged generic forward path runs.
        if (_symbolExtensions().Count > 0 && method == "workspace/symbol" && idNode is not null)
        {
            _ = HandleWorkspaceSymbolAsync(session, idNode.DeepClone(), json.DeepClone()!.AsObject());
            return;
        }

        switch (method)
        {
            case "initialize":
                JsonObject caps = await _initResult.Task.ConfigureAwait(false);
                await session.SendAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idNode?.DeepClone(),
                    ["result"] = caps.DeepClone(),
                }).ConfigureAwait(false);
                return;

            case "initialized":
            case "$/setTrace":
            case "$/cancelRequest":
                return; // swallow

            case "shutdown":
                await session.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode?.DeepClone(), ["result"] = null })
                    .ConfigureAwait(false);
                return;

            case "exit":
                session.Close();
                return;

            case "textDocument/didOpen":
            case "textDocument/didClose":
                if (DocUri(json) is { } su)
                {
                    if (method == "textDocument/didOpen") session.OpenUri(su); else session.CloseUri(su);
                }
                await HandleDocSyncAsync(method, json).ConfigureAwait(false);
                // A freshly-opened doc gets an immediate pull (often empty until the server computes) — the real set
                // arrives via the server's diagnostic/refresh. No disk resync: didOpen already carried the content.
                if (method == "textDocument/didOpen" && DocUri(json) is { } ou) TriggerDiagnostics(ou, resyncFromDisk: false);
                return;

            case "textDocument/didChange":
            case "textDocument/didSave":
                // We don't forward the client's buffer (disk stays the single source of truth — multi-agent safe).
                // Instead, re-sync the server's view from DISK and pull→publish diagnostics so edit feedback flows.
                if (DocUri(json) is { } cu) TriggerDiagnostics(cu, resyncFromDisk: true);
                return;

            default:
                if (idNode is not null && method is not null)
                {
                    // Request → namespace the id, forward to the LS, remember who to route the response to.
                    long gid = Interlocked.Increment(ref _nextGlobalId);
                    _pending[gid] = (session, idNode.DeepClone());
                    JsonObject clone = json.DeepClone()!.AsObject();
                    clone["id"] = gid;
                    await _lsWriter.WriteJsonAsync(clone, _ct).ConfigureAwait(false);
                }
                // client notifications other than the above are dropped (none are load-bearing for read-only nav)
                return;
        }
    }

    /// <summary>Synthesize a didClose for a disconnected client's open doc, releasing it from the shared ref-count.</summary>
    private async Task ReleaseOpenDocAsync(string uri)
    {
        try
        {
            var close = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/didClose",
                ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } },
            };
            await HandleDocSyncAsync("textDocument/didClose", close).ConfigureAwait(false);
        }
        catch (Exception ex) { _log($"release open doc {uri} failed: {ex.Message}"); }
    }

    /// <summary>Ref-count opens so one shared LS sees a doc opened once and closed when the last client drops it.</summary>
    private async Task HandleDocSyncAsync(string method, JsonNode json)
    {
        string? uri = json["params"]?["textDocument"]?["uri"]?.GetValue<string>();
        if (uri is null) return;

        if (method == "textDocument/didOpen")
        {
            int after = _openDocs.AddOrUpdate(uri, 1, (_, c) => c + 1);
            if (after == 1) await _lsWriter.WriteJsonAsync(json.DeepClone()!, _ct).ConfigureAwait(false);
        }
        else // didClose
        {
            if (!_openDocs.TryGetValue(uri, out int cur)) return;
            int after = cur - 1;
            if (after <= 0)
            {
                _openDocs.TryRemove(uri, out _);
                // Cancel any in-flight diagnostics work so a debounced resync/pull can't reach the server AFTER didClose
                // (a didChange/diagnostic for a closed document is a protocol violation the server may abort on).
                if (_diagDebounce.TryRemove(uri, out var cts)) { try { cts.Cancel(); cts.Dispose(); } catch { } }
                _docVersions.TryRemove(uri, out _);
                _lastErrorSig.TryRemove(uri, out _);
                await _lsWriter.WriteJsonAsync(json.DeepClone()!, _ct).ConfigureAwait(false);
            }
            else _openDocs[uri] = after;
        }
    }

    // ---- diagnostics bridge: pull (Roslyn) → publish (clients) ----------------------------------------------------
    // Microsoft.CodeAnalysis.LanguageServer serves PULL diagnostics (textDocument/diagnostic) and computes them only for
    // OPEN documents; Claude Code consumes PUSH (publishDiagnostics). This bridges the two: on open/edit, re-sync the
    // server's open buffer from DISK (the single source of truth — so concurrent agents never diverge), pull the
    // document's diagnostics, and broadcast them to every client as a publishDiagnostics notification.

    private static string? DocUri(JsonNode json) => json["params"]?["textDocument"]?["uri"]?.GetValue<string>();

    /// <summary>Debounced diagnostics refresh for a URI — coalesces a burst of edits into one pull+publish.
    /// <paramref name="resyncFromDisk"/> is true ONLY for client edits (didChange/didSave): it re-syncs the server's open
    /// buffer from disk. Refresh- and open-driven pulls pass false — a didChange there would make the server emit another
    /// refresh, looping forever.</summary>
    private void TriggerDiagnostics(string uri, bool resyncFromDisk)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        if (_diagDebounce.TryRemove(uri, out var old)) { try { old.Cancel(); old.Dispose(); } catch { } }
        _diagDebounce[uri] = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(150, cts.Token).ConfigureAwait(false); }
            catch { return; } // superseded by a newer edit
            try { await PublishDiagnosticsForAsync(uri, resyncFromDisk, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { _log($"diagnostics publish failed for {uri}: {ex.Message}"); }
            finally { if (_diagDebounce.TryGetValue(uri, out var cur) && ReferenceEquals(cur, cts)) _diagDebounce.TryRemove(uri, out _); }
        });
    }

    private async Task PublishDiagnosticsForAsync(string uri, bool resyncFromDisk, CancellationToken ct)
    {
        if (!_openDocs.ContainsKey(uri)) return; // only open docs yield pull diagnostics

        // 1. On a client edit, re-sync the server's buffer from DISK (Claude edits land on disk first; disk is the shared
        //    truth). NOT on refresh/open pulls — re-syncing there would loop. We replace the buffer with a didClose+didOpen
        //    rather than a full-document didChange: this Roslyn build's DidChange handler assumes INCREMENTAL changes and
        //    NREs (crashing its request queue) on a range-less full change. didOpen always carries the whole text, no range.
        if (resyncFromDisk)
        {
            string path = UriToPath(uri);
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                string langId = path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ? "vb" : "csharp";
                int version = _docVersions.AddOrUpdate(uri, 2, (_, v) => v + 1);
                await _lsWriter.WriteJsonAsync(Notify("textDocument/didClose",
                    new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }), ct).ConfigureAwait(false);
                await _lsWriter.WriteJsonAsync(Notify("textDocument/didOpen", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = langId, ["version"] = version, ["text"] = text },
                }), ct).ConfigureAwait(false);
            }
        }

        // 2. Pull diagnostics. A pull right after a didChange can come back as a ServerCancelled error (the server is
        //    still recomputing) — surfaced here as a null/kind-less result. Retry a few times before giving up; the
        //    refresh hook is the other path that re-pulls once the server signals completion.
        JsonNode? result = null;
        string? kind = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!_openDocs.ContainsKey(uri)) return; // closed mid-retry — stop touching it
            result = await PullDiagnosticsOnceAsync(uri, ct).ConfigureAwait(false);
            kind = result?["kind"]?.GetValue<string>();
            if (kind is "full" or "unchanged") break;           // a real report
            try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { return; }
        }

        // 3. A "full" report carries the current item set (empty = clear); "unchanged"/no-report → leave clients as-is.
        if (kind != "full") { _log($"diag[{(resyncFromDisk ? "edit" : "open/refresh")}] {uri} → no report (kind={kind}) — skip"); return; }
        JsonArray items = (result!["items"] as JsonArray)?.DeepClone()?.AsArray() ?? new JsonArray();
        await AugmentWithExtensionsAsync(uri, items, ct).ConfigureAwait(false);
        await PublishRoutedAsync(uri, items, resyncFromDisk).ConfigureAwait(false);
    }

    /// <summary>
    /// Serve a hover request with extension augmentation: forward it to Roslyn (internal id), gather each
    /// IHoverExtension's running-system hover, then reply to the client with the two merged. Resilient + time-bounded so a
    /// slow/down running system degrades to Roslyn-only hover rather than hanging the request.
    /// </summary>
    private async Task HandleHoverAsync(ClientSession session, JsonNode originalId, JsonObject request)
    {
        JsonNode? roslyn = null;
        try
        {
            long pid = Interlocked.Increment(ref _nextGlobalId);
            var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _internalPending[pid] = tcs;
            JsonObject fwd = request.DeepClone()!.AsObject();
            fwd["id"] = pid;
            await _lsWriter.WriteJsonAsync(fwd, _ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try { roslyn = await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
            finally { _internalPending.TryRemove(pid, out _); }
        }
        catch (Exception ex) { _log($"hover forward failed: {ex.Message}"); }

        string uri = request["params"]?["textDocument"]?["uri"]?.GetValue<string>() ?? "";
        int line = request["params"]?["position"]?["line"]?.GetValue<int>() ?? 0;
        int character = request["params"]?["position"]?["character"]?.GetValue<int>() ?? 0;
        string path; try { path = LspEdits.UriToPath(uri); } catch { path = uri; }

        var extras = new List<string>();
        foreach (IHoverExtension ext in _hoverExtensions())
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                string? h = await ext.GetHoverAsync(uri, path, line, character, cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(h)) extras.Add(h!);
            }
            catch (Exception ex) { _log($"hover extension {ext.GetType().Name} failed: {ex.Message}"); }
        }

        await session.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = originalId,
            ["result"] = MergeHover(roslyn, extras),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Serve a workspace/symbol request with extension augmentation: forward to Roslyn (internal id) and concurrently ask
    /// each ISymbolExtension for matches from its running system, then reply with the union. Time-bounded + isolated so a
    /// slow/down running system degrades to Roslyn-only symbols.
    /// </summary>
    private async Task HandleWorkspaceSymbolAsync(ClientSession session, JsonNode originalId, JsonObject request)
    {
        JsonNode? roslyn = null;
        try
        {
            long pid = Interlocked.Increment(ref _nextGlobalId);
            var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _internalPending[pid] = tcs;
            JsonObject fwd = request.DeepClone()!.AsObject();
            fwd["id"] = pid;
            await _lsWriter.WriteJsonAsync(fwd, _ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try { roslyn = await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
            finally { _internalPending.TryRemove(pid, out _); }
        }
        catch (Exception ex) { _log($"workspace/symbol forward failed: {ex.Message}"); }

        string query = request["params"]?["query"]?.GetValue<string>() ?? "";
        var merged = roslyn?.DeepClone()?.AsArray() ?? new JsonArray();
        foreach (ISymbolExtension ext in _symbolExtensions())
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                IReadOnlyList<ExtSymbol> hits = await ext.GetWorkspaceSymbolsAsync(query, cts.Token).ConfigureAwait(false);
                foreach (ExtSymbol s in hits)
                    merged.Add(new JsonObject
                    {
                        ["name"] = s.Name,
                        ["kind"] = (int)s.Kind,
                        ["containerName"] = s.Container,
                        ["location"] = new JsonObject
                        {
                            ["uri"] = s.LocationUri,
                            ["range"] = new JsonObject
                            {
                                ["start"] = new JsonObject { ["line"] = s.Line, ["character"] = s.Character },
                                ["end"] = new JsonObject { ["line"] = s.Line, ["character"] = s.Character },
                            },
                        },
                    });
            }
            catch (Exception ex) { _log($"symbol extension {ext.GetType().Name} failed: {ex.Message}"); }
        }

        await session.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = originalId,
            ["result"] = merged,
        }).ConfigureAwait(false);
    }

    /// <summary>Combine Roslyn's hover with extension hover parts into one markdown Hover (null if neither has anything).</summary>
    private static JsonNode? MergeHover(JsonNode? roslyn, List<string> extras)
    {
        if (extras.Count == 0) return roslyn?.DeepClone();
        string baseValue = roslyn?["contents"]?["value"]?.GetValue<string>()
            ?? (roslyn?["contents"] as JsonValue)?.GetValue<string>() ?? "";
        string combined = baseValue;
        foreach (string e in extras) combined += (combined.Length > 0 ? "\n\n---\n\n" : "") + e;
        var hover = new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = combined } };
        if (roslyn?["range"] is { } range) hover["range"] = range.DeepClone();
        return hover;
    }

    /// <summary>
    /// Merge any extension-contributed diagnostics (live state from a running system) into a document's diagnostic set,
    /// in place, before it's routed to clients. Each extension is isolated and time-bounded — one that's slow or throwing
    /// must never block or break the file's normal Roslyn diagnostics.
    /// </summary>
    private async Task AugmentWithExtensionsAsync(string uri, JsonArray items, CancellationToken ct)
    {
        IReadOnlyList<IDiagnosticExtension> diagExtensions = _diagExtensions();
        if (diagExtensions.Count == 0) return;
        string path;
        try { path = LspEdits.UriToPath(uri); } catch { path = uri; }
        foreach (IDiagnosticExtension ext in diagExtensions)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5)); // a stalled running-system call must not hold up edit feedback
                IReadOnlyList<ExtDiagnostic> extra = await ext.GetDiagnosticsAsync(uri, path, cts.Token).ConfigureAwait(false);
                string source = ext.GetType().Name;
                foreach (ExtDiagnostic d in extra)
                    items.Add(new JsonObject
                    {
                        ["range"] = new JsonObject
                        {
                            ["start"] = new JsonObject { ["line"] = d.Line, ["character"] = d.Character },
                            ["end"] = new JsonObject { ["line"] = d.EndLine, ["character"] = d.EndCharacter },
                        },
                        ["severity"] = (int)d.Severity,
                        ["code"] = d.Code,
                        ["message"] = d.Message,
                        ["source"] = source,
                    });
            }
            catch (Exception ex) { _log($"diagnostic extension {ext.GetType().Name} failed for {uri}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Route a document's diagnostics per-client so each Claude instance sees what it cares about and isn't flooded:
    ///  - a client that has the document OPEN gets the FULL set (errors + warnings + hints) — complete visibility;
    ///  - every OTHER client gets ERRORS ONLY, and only when that error set CHANGES — general visibility into a break in
    ///    a surrounding area (interdependencies matter) without the info/hint spam from a file it isn't working on.
    /// </summary>
    private async Task PublishRoutedAsync(string uri, JsonArray items, bool fromEdit)
    {
        // Full set → clients that have THIS uri open.
        byte[]? fullRaw = null; int openers = 0;
        foreach (var c in _clients.Values)
        {
            if (!c.HasOpen(uri)) continue;
            fullRaw ??= Encoding.UTF8.GetBytes(Notify("textDocument/publishDiagnostics",
                new JsonObject { ["uri"] = uri, ["diagnostics"] = items.DeepClone() }).ToJsonString());
            try { await c.SendRawAsync(fullRaw).ConfigureAwait(false); } catch { }
            openers++;
        }

        // Errors-only projection → non-openers, but only when it changed since last time (no per-refresh re-spam).
        var errors = new JsonArray();
        foreach (JsonNode? d in items)
            if (d?["severity"]?.GetValue<int>() == 1) errors.Add(d.DeepClone());
        int sig = ErrorSignature(errors);
        bool changed = !_lastErrorSig.TryGetValue(uri, out int prev) || prev != sig;
        _log($"diag[{(fromEdit ? "edit" : "open/refresh")}] {uri} → {items.Count} item(s) ({openers} opener(s), {errors.Count} error(s){(changed ? ", errors-changed" : "")})");
        if (!changed) return;          // surrounding-area error set is the same — don't re-broadcast to non-openers
        _lastErrorSig[uri] = sig;
        if (errors.Count == 0 && prev == 0) return; // never had errors and still none → nothing to clear

        byte[]? errRaw = null;
        foreach (var c in _clients.Values)
        {
            if (c.HasOpen(uri)) continue; // openers already got the full set
            errRaw ??= Encoding.UTF8.GetBytes(Notify("textDocument/publishDiagnostics",
                new JsonObject { ["uri"] = uri, ["diagnostics"] = errors.DeepClone() }).ToJsonString());
            try { await c.SendRawAsync(errRaw).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Order-independent signature of an error set (line+code+message), so we re-broadcast only on real change.</summary>
    private static int ErrorSignature(JsonArray errors)
    {
        int acc = 17;
        foreach (JsonNode? d in errors)
        {
            int line = d?["range"]?["start"]?["line"]?.GetValue<int>() ?? -1;
            string code = d?["code"]?.ToString() ?? "";
            string msg = d?["message"]?.GetValue<string>() ?? "";
            acc += HashCode.Combine(line, code, msg); // += keeps it order-independent
        }
        return acc;
    }

    /// <summary>One textDocument/diagnostic round-trip; returns the raw report result (null on error/timeout).</summary>
    private async Task<JsonNode?> PullDiagnosticsOnceAsync(string uri, CancellationToken ct)
    {
        long pid = Interlocked.Increment(ref _nextGlobalId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _internalPending[pid] = tcs;
        await _lsWriter.WriteJsonAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = pid,
            ["method"] = "textDocument/diagnostic",
            ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } },
        }, ct).ConfigureAwait(false);
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch { _internalPending.TryRemove(pid, out _); return null; }
    }

    private static string UriToPath(string uri)
    {
        try { return new Uri(uri).LocalPath; } catch { return uri; }
    }

    internal void OnSessionEnded(ClientSession session) => RemoveClient(session);

    // ---- workspace open + initialize (mirrors the bridge) ---------------------------------------------------------

    private async Task OpenWorkspaceAsync(string rootPath, string? solutionOverride)
    {
        WorkspaceTarget target = SolutionLocator.Locate(rootPath, solutionOverride, _log);
        if (!target.HasAny) { _log("no workspace target — solution/open skipped"); return; }

        JsonObject notification = target.SolutionPath is not null
            ? Notify("solution/open", new JsonObject { ["solution"] = PathToUri(target.SolutionPath) })
            : Notify("project/open", new JsonObject
            {
                ["projects"] = new JsonArray(target.ProjectPaths.Select(p => (JsonNode)PathToUri(p)!).ToArray()),
            });
        await _lsWriter.WriteJsonAsync(notification, _ct).ConfigureAwait(false);
        _log(target.SolutionPath is not null ? $"solution/open → {target.SolutionPath}" : $"project/open → {target.ProjectPaths.Count}");
    }

    private static JsonObject BuildInitialize(string rootPath)
    {
        string rootUri = PathToUri(rootPath);
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = rootUri,
                ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = "workspace" }),
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["configuration"] = true,
                        ["workspaceFolders"] = true,
                        ["symbol"] = new JsonObject(),       // workspace/symbol — resolve a type/namespace by name
                        // refreshSupport → the server tells us (workspace/diagnostic/refresh) when diagnostics recompute,
                        // so the bridge re-pulls at the right moment instead of racing the async computation.
                        ["diagnostics"] = new JsonObject { ["refreshSupport"] = true },
                    },
                    ["textDocument"] = new JsonObject
                    {
                        ["synchronization"] = new JsonObject { ["didSave"] = true },
                        ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                        ["hover"] = new JsonObject(),
                        ["definition"] = new JsonObject(),
                        ["typeDefinition"] = new JsonObject(),   // textDocument/typeDefinition — jump to a value's type
                        ["implementation"] = new JsonObject(),   // textDocument/implementation — interface/abstract impls
                        ["references"] = new JsonObject(),
                        ["rename"] = new JsonObject(),       // textDocument/rename — solution-wide rename
                        ["formatting"] = new JsonObject(),   // textDocument/formatting — whole-document format
                        // textDocument/codeAction — quick-fixes & refactorings; resolveProvider → we fetch the edit via
                        // codeAction/resolve before applying (Roslyn returns actions with `data` and a lazy `edit`).
                        ["codeAction"] = new JsonObject
                        {
                            ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray { "edit", "command" } },
                            ["dataSupport"] = true,
                        },
                        ["callHierarchy"] = new JsonObject(),    // prepareCallHierarchy + incoming/outgoing calls
                        ["typeHierarchy"] = new JsonObject(),    // prepareTypeHierarchy + super/sub types
                        ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false }, // pull diagnostics (textDocument/diagnostic)
                    },
                },
            },
        };
    }

    private static JsonObject Notify(string method, JsonObject @params) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params };

    private static long? ParseId(JsonNode idNode)
    {
        try { return idNode.GetValue<long>(); } catch { return null; }
    }

    private static string PathToUri(string path)
    {
        try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; } catch { return path; }
    }
}

/// <summary>
/// One connected client (a thin <c>lsp-client.cs</c> over a Sluice <see cref="IFrameChannel"/>; each frame is one LSP
/// message body — the frame boundary replaces Content-Length). A dedicated reader thread pulls inbound frames and
/// hands them to the multiplexer; a dedicated writer thread drains a BOUNDED outbound queue to the channel. The bound
/// is the multi-agent safety valve: a slow or wedged client fills its queue and gets dropped, it can never block the
/// shared server pump or stall the diagnostics broadcast to OTHER agents. Death detection watches the client's OS
/// process id (announced in a hello frame) — shared memory, unlike a pipe, gives no EOF when the peer exits.
/// </summary>
internal sealed class ClientSession
{
    private readonly IFrameChannel _channel;
    private readonly LspMultiplexer _mux;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _closed = new();
    private readonly BlockingCollection<byte[]> _outbound = new(boundedCapacity: 8192);
    private readonly ConcurrentDictionary<string, byte> _openUris = new(StringComparer.Ordinal); // docs THIS client has open
    private int _pidWatched;

    public int Id { get; }

    // Per-client open-document tracking drives diagnostic routing: a client gets a document's FULL diagnostics only when
    // it has that document open; otherwise it sees errors-only. Tracked here (not just the mux's shared ref-count) so the
    // routing is per-instance, and so a client that dies without didClose still has its opens released (see TakeOpenUris).
    public void OpenUri(string uri) => _openUris[uri] = 1;
    public void CloseUri(string uri) => _openUris.TryRemove(uri, out _);
    public bool HasOpen(string uri) => _openUris.ContainsKey(uri);

    /// <summary>Atomically drain and return the set of open URIs — used on disconnect to release each from the shared ref-count.</summary>
    public IReadOnlyCollection<string> TakeOpenUris()
    {
        var keys = _openUris.Keys.ToArray();
        _openUris.Clear();
        return keys;
    }

    public ClientSession(int id, IFrameChannel channel, LspMultiplexer mux, Action<string> log)
    {
        Id = id;
        _channel = channel;
        _mux = mux;
        _log = log;
    }

    public Task SendAsync(JsonNode node) => Enqueue(Encoding.UTF8.GetBytes(node.ToJsonString()));
    public Task SendRawAsync(ReadOnlyMemory<byte> raw) => Enqueue(raw.ToArray());

    // Non-blocking enqueue: a full queue means the client isn't draining (busy/stalled) — drop it rather than let it
    // back-pressure the shared pump. Returns a completed task so the mux's async write sites stay non-blocking.
    private Task Enqueue(byte[] frame)
    {
        try { if (!_outbound.TryAdd(frame)) { _log($"client {Id} outbound full — dropping"); Close(); } }
        catch { Close(); }
        return Task.CompletedTask;
    }

    public void Close()
    {
        try { _closed.Cancel(); } catch { }
        try { _outbound.CompleteAdding(); } catch { }
        try { _channel.Dispose(); } catch { }
    }

    public void Start(CancellationToken ct)
    {
        new Thread(() => ReadLoop(ct)) { IsBackground = true, Name = $"crlsp-cli-{Id}-rd" }.Start();
        new Thread(() => WriteLoop(ct)) { IsBackground = true, Name = $"crlsp-cli-{Id}-wr" }.Start();
    }

    private void ReadLoop(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var tok = linked.Token;
        try
        {
            while (!tok.IsCancellationRequested && _channel.WaitForFrame(tok))
            {
                while (_channel.TryReadFrame(out var span))
                {
                    byte[] raw = span.ToArray();   // copy out before AdvanceFrame frees the slot
                    _channel.AdvanceFrame();
                    if (raw.Length == 0) { return; }            // disconnect sentinel
                    if (TryHandleHello(raw)) continue;          // PID announce — not an LSP message
                    // A single malformed/unexpected message must not tear down the session (and, when this is the only
                    // client, take the whole daemon with it). Log and keep pumping.
                    try { _mux.HandleClientMessageAsync(this, new LspMessage { Raw = raw }).GetAwaiter().GetResult(); }
                    catch (Exception ex) { _log($"client {Id} message handler error (continuing): {ex}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"client {Id} read loop ended ({ex.GetType().Name})"); }
        finally { _mux.OnSessionEnded(this); Close(); }
    }

    private void WriteLoop(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        try
        {
            foreach (var frame in _outbound.GetConsumingEnumerable(linked.Token))
                _channel.WriteFrame(frame, linked.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* channel broke */ }
        finally { Close(); }
    }

    // The client's first frame announces its PID; we then reap the session when that process exits (shared memory has
    // no peer-death signal). Returns true if the frame was the hello (swallow it).
    private bool TryHandleHello(byte[] raw)
    {
        if (_pidWatched != 0) return false;
        try
        {
            if (JsonNode.Parse(raw)?[PipeKey.PidHelloKey]?.GetValue<int>() is int pid && pid > 0)
            {
                _pidWatched = pid;
                WatchProcess(pid);
                return true;
            }
        }
        catch { }
        return false;
    }

    private void WatchProcess(int pid) => _ = Task.Run(async () =>
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            await proc.WaitForExitAsync(_closed.Token).ConfigureAwait(false);
        }
        catch { /* already gone / inaccessible / cancelled */ }
        if (!_closed.IsCancellationRequested) _log($"client {Id} process {pid} exited — reaping");
        _mux.OnSessionEnded(this);
        Close();
    });
}
