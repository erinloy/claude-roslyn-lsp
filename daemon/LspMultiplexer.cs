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
    private readonly ConcurrentDictionary<string, string> _lastResultId = new(StringComparer.Ordinal);         // per-uri diagnostic resultId, replayed as previousResultId so the server can answer "unchanged"
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

    /// <summary>Open-document count as Roslyn currently sees it — the quantity that scales with connected agents.</summary>
    public int OpenDocCount => _openDocs.Count;

    // ---- idle-document eviction: the CURE for multi-agent retention ------------------------------------------------
    // THE MECHANISM. didOpen/didClose are ref-counted and a close is forwarded only when the LAST client drops. LSP
    // clients do not reliably send didClose for a file they merely READ, so the shared server accumulates the monotonic
    // UNION of every document any agent ever opened. Each open document pins syntax trees, a semantic model, and —
    // measured via gcdump 2026-08-03 — incremental SOURCE-GENERATOR state tables (IStateTable,
    // SourceGeneratorSyntaxTreeInfo, TableEntry<(SemanticModel, TypeDeclarationSyntax)>) which are keyed BY SYNTAX TREE,
    // per project, across a 345-project workspace. That is what took the server to 16.2 GB of LIVE gen-2 heap.
    //
    // WHY THIS IS LOSSLESS HERE, which is NOT true of eviction in a normal LSP host: this daemon NEVER FORWARDS THE
    // CLIENT'S BUFFER. didChange/didSave do not reach the server as edits — the handler above re-syncs the server's view
    // from DISK (resyncFromDisk: true), because disk is the single source of truth in a multi-agent fleet. So Roslyn's
    // copy of a document is ALWAYS disk-derived, and closing one discards nothing the client owns. The normal hazard —
    // evicting a dirty buffer and then applying an incremental change to stale text — cannot arise.
    //
    // RE-OPEN IS THE EXISTING PATH, not new code: any later didChange/didSave triggers resyncFromDisk, which already
    // issues didClose+didOpen carrying the whole text. An evicted document simply costs one re-read on next touch.
    // We close DIRECTLY to the server and leave _openDocs/_clients bookkeeping untouched, so client-facing protocol
    // state is unchanged and a client's eventual real didClose still behaves (its uri is simply already gone).
    //
    // Swept on didOpen rather than a timer: no new background task, no lifecycle to get wrong, and the sweep is
    // rate-limited so a burst of opens costs one pass. CLAUDE_ROSLYN_DOC_IDLE_MIN tunes it; 0 disables.
    private readonly ConcurrentDictionary<string, DateTime> _lastTouch = new(StringComparer.Ordinal);
    private DateTime _lastSweep = DateTime.UtcNow;
    private static readonly int DocIdleMinutes = ReadIdleMinutes();

    private static int ReadIdleMinutes()
    {
        var raw = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_DOC_IDLE_MIN");
        // A malformed value falls back to the DEFAULT, never to 0/disabled: a typo must not silently remove the cure.
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var m)) return m < 0 ? 0 : m;
        return 20;
    }

    private void Touch(string uri) => _lastTouch[uri] = DateTime.UtcNow;

    /// <summary>Drive the idle sweep from OUTSIDE the message path.
    ///
    /// <para>🔴 SWEEPING ONLY ON didOpen CANNOT FIRE WHEN IT MATTERS MOST. Documents become evictable precisely when
    /// agents go QUIET — and a quiet fleet sends no didOpen, so the sweep never runs and the retention it exists to
    /// reclaim sits there indefinitely. The trigger was anti-correlated with the condition: busy ⇒ sweeps but nothing
    /// is idle yet; idle ⇒ everything is evictable and nothing sweeps. Called from IdleShutdown's existing 30s poll,
    /// which runs regardless of traffic, so eviction now happens on a clock rather than on activity.</para></summary>
    public Task SweepIdleDocsAsync() => MaybeEvictIdleDocsAsync();

    private async Task MaybeEvictIdleDocsAsync()
    {
        if (DocIdleMinutes <= 0) return;
        if (DateTime.UtcNow - _lastSweep < TimeSpan.FromMinutes(1)) return; // rate-limit: one pass per minute at most
        _lastSweep = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(DocIdleMinutes);
        var evicted = 0;
        foreach (var uri in _openDocs.Keys)
        {
            // No recorded touch ⇒ opened before this build; treat as idle so pre-existing accumulation is reclaimed too.
            if (_lastTouch.TryGetValue(uri, out var t) && t > cutoff) continue;
            try
            {
                await _lsWriter.WriteJsonAsync(Notify("textDocument/didClose",
                    new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }), _ct).ConfigureAwait(false);
                _openDocs.TryRemove(uri, out _);
                _lastTouch.TryRemove(uri, out _);
                _docVersions.TryRemove(uri, out _);
                _lastErrorSig.TryRemove(uri, out _);
                _lastResultId.TryRemove(uri, out _);   // a re-opened document must not present a token from its previous life
                if (_diagDebounce.TryRemove(uri, out var cts)) { try { cts.Cancel(); cts.Dispose(); } catch { } }
                evicted++;
            }
            catch { /* a reclamation pass must never take down the multiplexer */ }
        }
        if (evicted > 0)
            _log($"evicted {evicted} document(s) idle >{DocIdleMinutes}m from the shared server ({_openDocs.Count} still open, " +
                 $"{_clients.Count} client(s)) — re-opens from disk on next touch");
    }
    public event Action? ClientCountChanged;

    /// <summary>Boot the single LS: pump its output, send initialize, capture caps, open the workspace.</summary>
    public async Task StartAsync(string rootPath, string? solutionOverride)
    {
        _ = Task.Run(() => PumpServerAsync());

        await _lsWriter.WriteJsonAsync(BuildInitialize(rootPath), _ct).ConfigureAwait(false);
        await _initResult.Task.ConfigureAwait(false); // wait for the server's initialize result (caps cached)
        await _lsWriter.WriteJsonAsync(Notify("initialized", new JsonObject()), _ct).ConfigureAwait(false);
        await OpenWorkspaceAsync(rootPath, solutionOverride).ConfigureAwait(false);
        // ARMED AFTER the first open, deliberately: arming before it would race the initial load and could re-open a
        // workspace that is still opening. The window between the two is one process start, and a project created in
        // it is picked up by the first change after — not lost.
        ArmProjectModelWatch(rootPath, solutionOverride);
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
                if (method == "textDocument/didOpen" && DocUri(json) is { } ou) { Touch(ou); TriggerDiagnostics(ou, resyncFromDisk: false); }
                await MaybeEvictIdleDocsAsync().ConfigureAwait(false);
                return;

            case "textDocument/didChange":
            case "textDocument/didSave":
                // We don't forward the client's buffer (disk stays the single source of truth — multi-agent safe).
                // Instead, re-sync the server's view from DISK and pull→publish diagnostics so edit feedback flows.
                if (DocUri(json) is { } cu) { Touch(cu); TriggerDiagnostics(cu, resyncFromDisk: true); }
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
                _lastResultId.TryRemove(uri, out _);   // a reopened document must not present a token from its previous life
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

        // 2b. Remember the resultId the server just issued — BOTH report kinds carry one, and it is what lets the NEXT
        // pull be answered "unchanged" instead of recomputed. Only ever a token the server gave us (see
        // BuildDiagnosticParams); dropped on close so a reopened document never presents a stale token.
        if (result?["resultId"]?.GetValue<string>() is { Length: > 0 } rid) _lastResultId[uri] = rid;

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
    /// <summary>Diagnostic pull params, carrying <c>previousResultId</c> when we have one for this uri.
    /// <para>THIS IS THE CHEAP-"unchanged" PATH AND IT IS HOW VISUAL STUDIO AVOIDS THIS COST. LSP pull diagnostics are
    /// designed so the client hands back the resultId it last received; if nothing affecting that document changed, the
    /// server replies <c>kind:"unchanged"</c> instead of recomputing and re-sending a full item set. Without the token the
    /// server has no way to know what we already hold, so EVERY pull must be answered with a full report — and this daemon
    /// pulls on every refresh, of every open document. MEASURED 2026-07-29: 199,484 pulls over 202 files (987 per file),
    /// all necessarily full. The consumer side already understood "unchanged" (see PublishDiagnosticsForAsync); only the
    /// request half was missing, so the capability was half-wired rather than absent.</para>
    /// <para>SAFE BY CONSTRUCTION: a server that ignores previousResultId simply returns a full report — today's
    /// behaviour. We only ever send a token the server itself gave us, and we drop it whenever a document closes.</para></summary>
    private JsonObject BuildDiagnosticParams(string uri)
    {
        var td = new JsonObject { ["uri"] = uri };
        var p = new JsonObject { ["textDocument"] = td };
        if (_lastResultId.TryGetValue(uri, out string? prev) && !string.IsNullOrEmpty(prev)) p["previousResultId"] = prev;
        return p;
    }

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
            ["params"] = BuildDiagnosticParams(uri),
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

    // ---- project-model watch --------------------------------------------------------------------------------------
    //
    // 🔴 THE PROJECT GRAPH WAS LOADED ONCE AND NEVER RELOADED, AND THE SERVER HAD NO WAY TO SAY SO.
    //
    // A language server has two staleness axes. FILE CONTENT is synced live — the client sends didOpen/didChange as an
    // agent reads and edits, so text is timely. The PROJECT MODEL was loaded exactly once, by the single
    // `OpenWorkspaceAsync` call in StartAsync. A new .csproj, a new ProjectReference, or a type MOVED between projects
    // was therefore invisible to a running daemon FOREVER.
    //
    // 🩸 MEASURED 2026-08-31 on the Ziltch tree, which is what prompted this:
    //     daemon started              08-30 20:04:00
    //     Ziltch.Render.Shape.csproj  08-31 00:24:45      4h20m AFTER the daemon
    //     `ShapeSpec` moved into that new project ⇒ every use reported CS0103 "does not exist in the current
    //     context". `dotnet build` on the same tree: 0 errors.
    // THREE AGENTS published a compile break from those diagnostics that night and all three retracted. The server was
    // not wrong — it answered about the solution it had loaded, and nothing could tell it the solution had changed.
    //
    // ⚖️ WHY A RE-OPEN AND NOT `workspace/didChangeWatchedFiles`. The watched-files route needs the client to declare
    // the capability AND honour every registration the server asks for; this daemon answers `registerCapability` with a
    // null result (see the server→client request handling above), so declaring it would promise a watch nobody
    // performs — a capability claimed and not honoured is worse than one absent, because the server then stops
    // compensating. `solution/open` is Roslyn's OWN documented load path and is already the mechanism this file uses;
    // re-sending it is the same instruction the daemon gives at startup, so there is no second code path to keep true.
    //
    // ⚠️ obj/ AND bin/ ARE EXCLUDED, and that exclusion is load-bearing rather than tidy: the SDK writes
    // `<Project>.csproj.nuget.g.props` and copies project artefacts under obj/ DURING a build, so watching them would
    // fire a reload on every compile — a reload storm triggered by the very builds the server exists to support.
    private readonly List<FileSystemWatcher> _projectWatchers = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private Timer? _reloadDebounce;
    private string? _watchRoot;
    private string? _watchSolutionOverride;
    private int _reloadCount;

    /// <summary>Watch the workspace for PROJECT-MODEL changes and re-open the solution when one lands.</summary>
    private void ArmProjectModelWatch(string rootPath, string? solutionOverride)
    {
        _watchRoot = rootPath;
        _watchSolutionOverride = solutionOverride;

        foreach (string pattern in new[] { "*.csproj", "*.sln", "*.slnx" })
        {
            try
            {
                var w = new FileSystemWatcher(rootPath, pattern)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                };
                w.Created += OnProjectFileEvent;
                w.Changed += OnProjectFileEvent;
                w.Deleted += OnProjectFileEvent;
                w.Renamed += OnProjectFileEvent;
                // 🩸 A DROPPED BUFFER MUST NOT BE IGNORED — AND MUST NOT RELOAD BLINDLY EITHER. This used to call
                // ScheduleReload() unconditionally, and that is the one path the obj/bin exclusion above cannot reach:
                // the filter lives in OnProjectFileEvent, the overflow never goes through it, so build churn under
                // bin/ and obj/ FILLED THE OS BUFFER and every overflow re-opened a 357-project solution. The comment
                // above says that exclusion exists to prevent "a reload storm triggered by the very builds the server
                // exists to support" — and the storm arrived anyway, through the door the filter does not cover.
                //
                // 📏 MEASURED 2026-09-09 on the canonical Ziltch daemon (@blackmagic found it, @mesh confirmed on an
                // independent 40 MB tail of the 1.14 GB log):
                //     "Too many changes at once"   61,029   — 24% of every line in the sample
                //     "solution/open"              10,020   — the 357-project graph, re-opened, in one window
                // Four agents spent an evening diagnosing the resulting latency as a wedged daemon.
                //
                // ⚖️ SO THE SAFE DIRECTION IS PRESERVED BY LOOKING, NOT BY ASSUMING. "I missed some events" and
                // "nothing happened" are still the same silence — so on overflow we go and READ the project-file set
                // instead of trusting it. A re-scan cannot miss a real change (it observes current truth, not events)
                // and cannot storm on churn it excludes. That is CANON's ranking: by construction over machinery.
                w.Error += (_, e) =>
                {
                    // OFF THE WATCHER'S CALLBACK THREAD. The re-scan walks the tree (~40 s measured here), and doing
                    // that inline would block the very thread that delivers change events — starving the watcher
                    // during exactly the window it is already losing events in, and deepening the overflow it is
                    // reacting to. The latch inside makes the pile-up harmless.
                    _log($"project-model watch: buffer error ({e.GetException().Message}) — re-scanning the project set");
                    _ = Task.Run(OnWatchOverflow);
                };
                // Machinery, secondary and stated as such: a bigger buffer makes overflow RARER, never impossible, so
                // it is not the fix — the re-scan above is. 64 KiB is the documented maximum.
                w.InternalBufferSize = 64 * 1024;
                w.EnableRaisingEvents = true;
                _projectWatchers.Add(w);
            }
            catch (Exception ex)
            {
                // Not fatal: the daemon still serves, it just goes back to load-once behaviour. Say so LOUDLY rather
                // than degrading quietly — a silent fallback here restores the exact defect this method removes.
                _log($"project-model watch: FAILED to watch '{pattern}' under '{rootPath}' ({ex.Message}). " +
                     "The project graph will NOT reload; structural diagnostics may go stale without warning.");
            }
        }

        // SEED THE COMPARISON SUBJECT. Without this the first overflow compares against an EMPTY set, finds a
        // difference of N, and reloads — which would reproduce the storm for exactly one cycle per daemon and, worse,
        // would make the re-scan look like it does not work.
        if (_projectWatchers.Count > 0)
            _log($"project-model watch ARMED on {rootPath} ({_projectWatchers.Count} patterns) "
               + "— solution/open re-sent on a REAL change; a buffer overflow re-scans instead of assuming");

        // OFF THE STARTUP PATH, for the same reason the overflow scan is off the callback thread: this walk takes
        // SECONDS on a tree this size, and the daemon's whole purpose is that the first call of every agent session is
        // fast. Until the seed lands the known set is empty, so an overflow arriving in that window finds a mismatch
        // and reloads — which is precisely the OLD behaviour, i.e. the failure mode of an unseeded start is "no better
        // than before", never "worse".
        _ = Task.Run(() =>
        {
            try
            {
                var seed = SnapshotProjectFiles(rootPath);
                lock (_knownLock) _knownProjectFiles = seed;
                _log($"project-model watch: baseline captured — {seed.Count} project files under {rootPath} "
                   + "(bin/obj/.git/node_modules excluded). Overflows now compare instead of assuming.");
            }
            catch (Exception ex)
            {
                _log($"project-model watch: could not seed the project-file set ({ex.Message}). Overflows will reload "
                   + "unconditionally, as they did before the re-scan landed.");
            }
        });
    }

    /// <summary>The project-file set as last loaded: path → last-write UTC. The subject a buffer overflow asks about,
    /// so an overflow can be answered by COMPARISON instead of by assumption.</summary>
    private Dictionary<string, DateTime> _knownProjectFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _knownLock = new();
    private int _overflowQuiet;
    /// <summary>Single-flight latch for the overflow re-scan. 0 = idle, 1 = a walk is in progress. Overflows arriving
    /// during a walk are DROPPED rather than queued: they ask the identical question ("did the project set move?") and
    /// the in-flight walk observes current truth, so it already answers the ones that land while it runs.</summary>
    private int _overflowScanBusy;

    /// <summary>
    /// 🔑 <b>WHAT A BUFFER OVERFLOW ACTUALLY MEANS: "I stopped being able to tell you." Not "something changed".</b>
    /// So this goes and looks, and reloads only if the project-file set really moved.
    ///
    /// <para>The safety property is UNCHANGED and is why a re-scan is the right shape rather than a suppression: a
    /// scan reads CURRENT TRUTH, not a stream of events, so it cannot miss a change that happened during the blind
    /// window — which is exactly what the old blind reload was protecting against. It simply declines to reload when
    /// nothing moved.</para>
    ///
    /// <para>⚠️ ANY FAILURE RELOADS. If the scan throws — a permission fault, a directory vanishing mid-walk — we are
    /// back to "I cannot tell", and the safe direction is the original one. A cheaper wrong answer is not the trade.</para>
    /// </summary>
    private void OnWatchOverflow()
    {
        if (_watchRoot is null) { ScheduleReload(); return; }

        // 🩸 THE SCAN IS NOT CHEAP AND MY FIRST VERSION RAN IT PER OVERFLOW, WHICH WAS WORSE THAN THE BUG.
        // Measured on this tree 2026-09-09 BEFORE shipping it: 5,452 project files, and a full walk that takes SECONDS
        // (~40 s through an equivalent PowerShell walk; the C# one below is faster, but it is the same order and it is
        // emphatically not free — the number that matters is "seconds", not the exact figure) — and
        // overflows arrive in the tens of thousands (61,029 in one 40 MB log sample). Per-overflow scanning would have
        // replaced a 357-project reload with something an order of magnitude worse, on the hotter path. The re-scan is
        // only correct BECAUSE it is single-flighted and debounced: an overflow storm collapses to ONE walk, and a
        // walk already in progress absorbs every overflow that lands during it (they ask the same question, and it is
        // already being answered).
        if (Interlocked.CompareExchange(ref _overflowScanBusy, 1, 0) != 0) return;

        Dictionary<string, DateTime> now;
        try { now = SnapshotProjectFiles(_watchRoot); }
        catch (Exception ex)
        {
            _log($"project-model watch: overflow re-scan FAILED ({ex.Message}) — reloading on the safe side");
            ScheduleReload();
            return;
        }
        finally { Interlocked.Exchange(ref _overflowScanBusy, 0); }

        bool changed;
        lock (_knownLock)
        {
            changed = _knownProjectFiles.Count != now.Count
                   || now.Any(kv => !_knownProjectFiles.TryGetValue(kv.Key, out var was) || was != kv.Value);
            if (changed) _knownProjectFiles = now;
        }

        if (!changed)
        {
            // Counted, not silent: if this number climbs while nothing reloads, the watcher is being drowned by churn
            // it correctly ignores — which is a fact about the BOX (20 agents building into one tree), not a fault here.
            int q = Interlocked.Increment(ref _overflowQuiet);
            if (q == 1 || q % 100 == 0)
                _log($"project-model watch: overflow #{q} carried NO project-file change — not reloading "
                   + $"({now.Count} project files unchanged). Build churn under bin/obj fills the buffer; the model did not move.");
            return;
        }

        _log("project-model watch: overflow carried a REAL project-file change — reloading");
        ScheduleReload();
    }

    /// <summary>Every project file under <paramref name="root"/>, skipping the directories whose churn caused the
    /// overflow in the first place. Walked manually rather than with AllDirectories: enumerating bin/ and obj/ only to
    /// discard them is the same wasted work, and on this tree they hold far more files than the source does.</summary>
    private static Dictionary<string, DateTime> SnapshotProjectFiles(string root)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(f);
                    if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                        map[f] = File.GetLastWriteTimeUtc(f);
                }

                foreach (string d in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(d);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                        continue;
                    stack.Push(d);
                }
            }
            catch (UnauthorizedAccessException) { /* one unreadable directory is not the whole answer — keep walking */ }
            catch (DirectoryNotFoundException) { /* raced with a delete; the next overflow re-reads */ }
        }

        return map;
    }

    private void OnProjectFileEvent(object sender, FileSystemEventArgs e)
    {
        string p = e.FullPath;
        if (p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return;

        ScheduleReload();
    }

    /// <summary>
    /// Coalesce a burst into ONE reload. A solution edit, a `dotnet new`, or a branch switch writes several project
    /// files in quick succession; reloading per event would re-read the whole graph N times and each read is the
    /// expensive operation this daemon exists to amortise.
    /// </summary>
    private void ScheduleReload()
    {
        try
        {
            _reloadDebounce ??= new Timer(_ => _ = ReloadWorkspaceAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _reloadDebounce.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task ReloadWorkspaceAsync()
    {
        if (_watchRoot is null) return;
        if (!await _reloadGate.WaitAsync(0).ConfigureAwait(false)) return;   // a reload is already running; its re-read covers this change
        try
        {
            int n = Interlocked.Increment(ref _reloadCount);
            _log($"project-model changed — re-opening workspace (reload #{n})");
            await OpenWorkspaceAsync(_watchRoot, _watchSolutionOverride).ConfigureAwait(false);
            // RE-BASELINE AFTER THE LOAD, not before it: the set this daemon is now serving is the one a later
            // overflow must be compared against. Taking it before the re-open would compare the next overflow against
            // a graph we never loaded.
            try
            {
                var after = SnapshotProjectFiles(_watchRoot);
                lock (_knownLock) _knownProjectFiles = after;
            }
            catch { /* a stale baseline only costs an extra reload, never a missed change */ }
        }
        catch (Exception ex)
        {
            // The daemon must survive a failed reload — a dead daemon is worse than a stale one, and the next change
            // schedules another attempt. Reported rather than swallowed so a persistently failing reload is visible.
            _log($"project-model reload FAILED: {ex.Message}. The graph may be stale until the next change.");
        }
        finally { _reloadGate.Release(); }
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
