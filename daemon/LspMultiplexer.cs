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
    private readonly ConcurrentDictionary<int, ClientSession> _clients = new();
    private readonly ConcurrentDictionary<string, int> _openDocs = new(StringComparer.Ordinal);
    private long _nextGlobalId = 1; // 0 reserved for the daemon's own initialize
    private int _nextClientId = 0;

    public LspMultiplexer(Stream lsIn, Stream lsOut, Action<string> log, CancellationToken ct)
    {
        _lsWriter = new LspMessageWriter(lsIn);
        _lsReader = new LspMessageReader(lsOut);
        _log = log;
        _ct = ct;
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
                await HandleDocSyncAsync(method, json).ConfigureAwait(false);
                return;

            case "textDocument/didChange":
            case "textDocument/didSave":
                return; // disk is the source of truth; the LS file-watches

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
                await _lsWriter.WriteJsonAsync(json.DeepClone()!, _ct).ConfigureAwait(false);
            }
            else _openDocs[uri] = after;
        }
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
                    },
                    ["textDocument"] = new JsonObject
                    {
                        ["synchronization"] = new JsonObject { ["didSave"] = true },
                        ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                        ["hover"] = new JsonObject(),
                        ["definition"] = new JsonObject(),
                        ["references"] = new JsonObject(),
                        ["rename"] = new JsonObject(),       // textDocument/rename — solution-wide rename
                        ["formatting"] = new JsonObject(),   // textDocument/formatting — whole-document format
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
    private int _pidWatched;

    public int Id { get; }

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
                    _mux.HandleClientMessageAsync(this, new LspMessage { Raw = raw }).GetAwaiter().GetResult();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"client {Id} read loop ended: {ex.Message}"); }
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
