// DaemonRouter — lets ONE Claude instance work across repositories beyond its home location.
//
// The home workspace's daemon owns the home repo's solution. When Claude opens a file that lives OUTSIDE the home root
// (a sibling repo, an additional working directory), that file isn't in the home solution, so the home daemon can only
// analyze it as a loose file — no project references, hence false "type not found" errors. The fix: route each file to
// the daemon for ITS OWN workspace. A single client multiplexes N daemons, one per distinct repo it touches, and every
// file gets full project-aware analysis from the daemon that actually owns it.
//
// This is tractable because the daemon answers every server->client request ITSELF (configuration, registerCapability,
// diagnostic/refresh) and never forwards them to clients (see LspMultiplexer.AnswerServerRequestAsync). So a client only
// ever receives RESPONSES (which carry Claude's own globally-unique request id) and NOTIFICATIONS (which carry a uri).
// Responses route back to Claude unchanged — no cross-daemon id namespacing needed. The only frame a secondary daemon
// sends that Claude must NOT see is the response to our own synthetic `initialize`, which we drop by its sentinel id.
//
// Routing key is the file's workspace root: the nearest ancestor with a .git / .sln / .slnx. Files under the home root
// stay on the primary daemon (unchanged, zero-risk path); only out-of-home files spin up a secondary.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeRoslynLsp.Bridge;
using Sluice;

namespace ClaudeRoslynLsp.Client;

internal sealed class DaemonRouter : IDisposable
{
    // Sentinel id for the synthetic `initialize` we send each secondary; its response is dropped (never reaches Claude).
    private const long InitSentinel = -918273645;

    private readonly IFrameChannel _primary;
    private readonly string _homeRoot;          // normalized: backslashes, lowercased, no trailing separator
    private readonly string _pluginRoot;
    private readonly LspMessageWriter _writer;
    private readonly Action<string> _log;
    private readonly Action _onPrimaryDeath;     // invoked when the home daemon dies → client exits, Claude restarts the LSP
    private readonly CancellationToken _ct;
    private readonly object _writeLock = new();  // multiple down-pumps share the one stdout writer

    private readonly ConcurrentDictionary<string, string?> _uriRoot = new(StringComparer.OrdinalIgnoreCase); // uri → foreign root (null = home)
    private readonly ConcurrentDictionary<string, Secondary> _secondaries = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _multiRepo;            // false → every file stays on the primary (kill-switch), watch still active

    // ---- request watchdog --------------------------------------------------------------------------------------------
    //
    // Daemon DEATH is already handled (WatchDaemon → exit → the host restarts us). The other, subtler wedge is a daemon
    // that is ALIVE and simply never answers: shared memory carries no per-request failure signal, so a request the
    // language server accepts and drops parks the host's LSP call FOREVER (observed: a 2h+ hang the user had to kill).
    // A relay must therefore bound every request it carries, exactly as RoslynDaemonClient bounds its own. When a request
    // exceeds the ceiling we cancel it upstream and synthesize the JSON-RPC error response the host is waiting on — so an
    // LSP call ALWAYS terminates: with an answer, or with an error, never with an unbounded wait.
    // Override with CRLSP_LSP_REQUEST_TIMEOUT_SECONDS; <=0 disables the bound (explicit escape hatch).
    private static readonly TimeSpan RequestCeiling = ResolveRequestCeiling();

    private static TimeSpan ResolveRequestCeiling()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("CRLSP_LSP_REQUEST_TIMEOUT_SECONDS"), out int s))
            return s > 0 ? TimeSpan.FromSeconds(s) : Timeout.InfiniteTimeSpan;
        return TimeSpan.FromSeconds(180); // generous: covers a cold workspace load + heavy solution-wide ops
    }

    private sealed record Pending(string Method, IFrameChannel? Channel, long StartedTicks);

    private readonly ConcurrentDictionary<long, Pending> _inflight = new();   // id → request awaiting an answer
    private readonly ConcurrentDictionary<long, long> _abandoned = new();     // id → tick we errored it (drop a late answer)
    private readonly Timer? _watchdog;

    public DaemonRouter(IFrameChannel primary, string homeRoot, string pluginRoot, LspMessageWriter writer,
        Action<string> log, CancellationToken ct, Action onPrimaryDeath, bool multiRepo)
    {
        _primary = primary;
        _homeRoot = Normalize(homeRoot);
        _pluginRoot = pluginRoot;
        _writer = writer;
        _log = log;
        _ct = ct;
        _onPrimaryDeath = onPrimaryDeath;
        _multiRepo = multiRepo;
        _watchdog = RequestCeiling == Timeout.InfiniteTimeSpan
            ? null
            : new Timer(_ => { try { SweepInflight(); } catch { } }, null, 2000, 2000);
        StartDownPump(primary, filterInit: false, isPrimary: true); // primary relays everything incl. Claude's real init response
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        foreach (var kv in _secondaries)
            if (kv.Value.Channel is { } ch) { try { ch.Dispose(); } catch { } }
    }

    /// <summary>Route one Claude→daemon message to the daemon that owns its file (home or a per-repo secondary).</summary>
    public void Up(LspMessage msg)
    {
        string? foreignRoot = ResolveForeignRoot(msg);
        if (foreignRoot is null) { TrackRequest(msg, _primary); WriteTo(_primary, msg.Raw); return; }   // home file / no uri → primary

        Secondary sec = _secondaries.GetOrAdd(foreignRoot, r => new Secondary(r));
        TrackRequest(msg, sec.Channel);   // may still be connecting: null channel → we can't cancel upstream, but we still answer Claude
        sec.Send(msg.Raw, this);
    }

    // Record an outbound REQUEST (carries BOTH id and method) so the watchdog can bound it. Notifications (no id) and
    // Claude's own responses (no method) are fire-and-forget. `initialize`/`shutdown` are exempt: a cold daemon
    // legitimately takes minutes to load the solution, and the LSP host owns that wait via its own startupTimeout.
    private void TrackRequest(LspMessage msg, IFrameChannel? channel)
    {
        if (_watchdog is null) return;
        JsonNode? j;
        try { j = msg.Json; } catch { return; }
        if (j?["method"]?.GetValue<string>() is not { } method) return;

        if (method == "$/cancelRequest")   // Claude cancelled it itself — stop watching that id
        {
            if (long.TryParse(j["params"]?["id"]?.ToString(), out long cancelled)) _inflight.TryRemove(cancelled, out _);
            return;
        }
        if (method is "initialize" or "shutdown") return;
        if (j["id"] is not { } idNode || !long.TryParse(idNode.ToString(), out long id)) return;
        _inflight[id] = new Pending(method, channel, Environment.TickCount64);
    }

    // Fail every request past the ceiling: cancel it upstream (best effort) and answer Claude with a JSON-RPC error, so
    // the call returns instead of hanging. The id goes on the abandoned list so a late answer is dropped, never delivered
    // twice. Runs on a timer thread; never throws (the caller swallows).
    private void SweepInflight()
    {
        long now = Environment.TickCount64;
        long ceilMs = (long)RequestCeiling.TotalMilliseconds;

        foreach (var kv in _inflight)
        {
            if (now - kv.Value.StartedTicks < ceilMs) continue;
            if (!_inflight.TryRemove(kv.Key, out Pending? p)) continue;
            _abandoned[kv.Key] = now;
            _log($"request '{p.Method}' (id {kv.Key}) unanswered after {RequestCeiling.TotalSeconds:0}s — cancelling it and failing the call");

            if (p.Channel is { } ch)
                WriteTo(ch, Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "$/cancelRequest", ["params"] = new JsonObject { ["id"] = kv.Key },
                }.ToJsonString()));

            WriteDown(Encoding.UTF8.GetBytes(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = kv.Key,
                ["error"] = new JsonObject
                {
                    ["code"] = -32603,
                    ["message"] = $"roslyn daemon did not answer '{p.Method}' within {RequestCeiling.TotalSeconds:0}s — " +
                                  "the workspace may still be loading, or the language server wedged on this request; " +
                                  "retry shortly, or restart the daemon",
                },
            }.ToJsonString()));
        }

        foreach (var kv in _abandoned)   // forget abandoned ids once no plausible late answer can still arrive
            if (now - kv.Value > 300_000) _abandoned.TryRemove(kv.Key, out _);
    }

    // ---- diagnostic severity filter ----------------------------------------------------------------------------------
    //
    // Roslyn publishes IDE style suggestions (IDE0001 "Name can be simplified", IDE1006 naming, CA2007 ConfigureAwait) at
    // the same severity channel as real errors. An LSP host that surfaces every published diagnostic to an agent spends a
    // large share of its context on hints about files it merely touched — hundreds of lines per edit, none actionable, and
    // it crowds out the errors that are. Filter at the relay so only diagnostics at or above the threshold reach the host.
    // CRLSP_DIAGNOSTIC_MIN_SEVERITY: 1=error, 2=warning (default), 3=info, 4=hint. 4 restores the unfiltered stream.
    private static readonly int MinSeverity = ResolveMinSeverity();

    private static int ResolveMinSeverity()
        => int.TryParse(Environment.GetEnvironmentVariable("CRLSP_DIAGNOSTIC_MIN_SEVERITY"), out int s) && s is >= 1 and <= 4
            ? s
            : 2; // errors + warnings: everything that means the code is wrong, nothing that means it could be prettier

    // Rewrite a publishDiagnostics notification to drop below-threshold entries. Filters the ARRAY rather than dropping the
    // frame — a file with one error and forty hints must still deliver the error. An empty result is still published (that
    // is how the host learns a file went clean). Any non-diagnostic frame passes straight through, unparsed.
    private static byte[] FilterDiagnostics(byte[] frame)
    {
        if (MinSeverity >= 4) return frame;
        // Cheap pre-check: only the diagnostics notification is worth parsing, and it is a small fraction of frames.
        if (frame.AsSpan().IndexOf("publishDiagnostics"u8) < 0) return frame;
        try
        {
            JsonNode? j = JsonNode.Parse(frame);
            if (j?["method"]?.GetValue<string>() != "textDocument/publishDiagnostics") return frame;
            if (j["params"]?["diagnostics"] is not JsonArray diags || diags.Count == 0) return frame;

            var kept = new JsonArray();
            foreach (JsonNode? d in diags)
            {
                // An absent severity means "undefined" in LSP, which callers treat as error — keep it.
                int sev = d?["severity"]?.GetValue<int>() ?? 1;
                if (sev <= MinSeverity) kept.Add(d!.DeepClone());
            }
            if (kept.Count == diags.Count) return frame;   // nothing filtered — hand back the original bytes
            j["params"]!["diagnostics"] = kept;
            return Encoding.UTF8.GetBytes(j.ToJsonString());
        }
        catch { return frame; }   // never let a malformed frame cost the host its diagnostics
    }

    // A frame the daemon sent: true if it answers a request we are (or were) tracking. Clears the inflight entry, and
    // reports "drop" for an id we already errored so Claude never sees two responses for one request.
    private bool IsStaleAnswer(byte[] frame)
    {
        if (_watchdog is null || (_inflight.IsEmpty && _abandoned.IsEmpty)) return false;
        try
        {
            JsonNode? j = JsonNode.Parse(frame);
            if (j?["method"] is not null || j?["id"] is not { } idNode) return false;   // not a response
            if (!long.TryParse(idNode.ToString(), out long id)) return false;
            _inflight.TryRemove(id, out _);
            return _abandoned.TryRemove(id, out _);   // late answer to a request we already failed → drop it
        }
        catch { return false; }
    }

    // ---- secondary lifecycle -----------------------------------------------------------------------------------------

    internal async Task ConnectSecondaryAsync(Secondary sec)
    {
        try
        {
            string endpoint = PipeKey.ForRoot(sec.Root);
            _log($"multi-repo: connecting secondary daemon for {sec.Root}");
            IFrameChannel? ch = await DaemonConnector.ConnectAsync(endpoint, sec.Root, null, _pluginRoot, _log, _ct).ConfigureAwait(false);
            if (ch is null)
            {
                _log($"multi-repo: secondary connect FAILED for {sec.Root} — routing those files to the primary (loose) instead");
                sec.FallBack(_primary, this);
                return;
            }
            // Announce our PID (the daemon reaps the session by watching this process), then drive the per-client
            // initialize handshake with a sentinel id so the down-pump can drop the caps response before it reaches Claude.
            WriteTo(ch, Encoding.UTF8.GetBytes($"{{\"{PipeKey.PidHelloKey}\":{Environment.ProcessId}}}"));
            WriteTo(ch, Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{InitSentinel},\"method\":\"initialize\",\"params\":{{}}}}"));
            WriteTo(ch, Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}"));
            StartDownPump(ch, filterInit: true, isPrimary: false);
            sec.MarkReady(ch, this);
            _log($"multi-repo: secondary ready for {sec.Root}");
        }
        catch (Exception ex) { _log($"multi-repo: secondary error {sec.Root}: {ex.Message}"); sec.FallBack(_primary, this); }
    }

    // ---- transport ---------------------------------------------------------------------------------------------------

    internal void WriteTo(IFrameChannel channel, byte[] raw)
    {
        try { channel.WriteFrame(raw, _ct); } catch (Exception ex) { _log($"write failed: {ex.Message}"); }
    }

    /// <summary>Write one message body down to Claude, re-framed with Content-Length. The ONE stdout seam — the down-pumps
    /// and the watchdog's synthetic error responses all go through it, so they can never interleave mid-frame.</summary>
    private void WriteDown(byte[] frame)
    {
        try { lock (_writeLock) { _writer.WriteRawAsync(frame, _ct).GetAwaiter().GetResult(); } }
        catch (Exception ex) { _log($"downstream write failed: {ex.Message}"); }
    }

    private void StartDownPump(IFrameChannel channel, bool filterInit, bool isPrimary)
    {
        var t = new Thread(() =>
        {
            bool wantInit = filterInit; // still waiting to drop our synthetic-initialize response
            bool wantPid = true;        // still waiting to intercept the daemon's PID hello
            try
            {
                while (!_ct.IsCancellationRequested && channel.WaitForFrame(_ct))
                {
                    while (channel.TryReadFrame(out var span))
                    {
                        byte[] frame = span.ToArray();   // copy out before AdvanceFrame frees the ring slot
                        channel.AdvanceFrame();
                        if (wantPid && TryDaemonPid(frame, out int dpid)) { wantPid = false; WatchDaemon(dpid, isPrimary, channel); continue; }
                        if (wantInit && IsInitSentinelResponse(frame)) { wantInit = false; continue; } // drop our synthetic init response
                        if (IsStaleAnswer(frame)) continue;   // clears the watchdog entry; drops a late answer we already errored
                        WriteDown(FilterDiagnostics(frame));  // re-frame with Content-Length to Claude, style-noise removed
                    }
                }
            }
            catch { /* channel closed / cancelled */ }
        }) { IsBackground = true, Name = isPrimary ? "crlsp-down" : "crlsp-down-sec" };
        t.Start();
    }

    // Watch the daemon process for THIS channel; on its death detect the dead ring shared memory can't signal. Primary
    // death → exit the whole client (Claude restarts the LSP → reconnect to the live daemon). Secondary death → drop just
    // that secondary; its files reconnect to a fresh daemon on next use.
    private void WatchDaemon(int pid, bool isPrimary, IFrameChannel channel)
    {
        _log($"watching {(isPrimary ? "primary" : "secondary")} daemon pid {pid} for death");
        var t = new Thread(() =>
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(); } catch { /* already gone / not queryable */ }
            if (_ct.IsCancellationRequested) return;
            if (isPrimary) { _log($"primary daemon (pid {pid}) exited — exiting so Claude Code restarts the LSP and reconnects"); _onPrimaryDeath(); }
            else { _log($"secondary daemon (pid {pid}) exited — dropping it; its files reconnect on next use"); DropSecondary(channel); }
        }) { IsBackground = true, Name = "crlsp-dwatch" };
        t.Start();
    }

    private void DropSecondary(IFrameChannel channel)
    {
        foreach (var kv in _secondaries)
            if (ReferenceEquals(kv.Value.Channel, channel) && _secondaries.TryRemove(kv.Key, out _))
            {
                try { channel.Dispose(); } catch { }
                break;
            }
    }

    private static bool TryDaemonPid(byte[] frame, out int pid)
    {
        pid = 0;
        try { if (JsonNode.Parse(frame)?[PipeKey.DaemonPidKey]?.GetValue<int>() is int p && p > 0) { pid = p; return true; } }
        catch { }
        return false;
    }

    private static bool IsInitSentinelResponse(byte[] frame)
    {
        try
        {
            JsonNode? j = JsonNode.Parse(frame);
            return j?["method"] is null && j?["id"] is { } id && id.GetValue<long>() == InitSentinel;
        }
        catch { return false; }
    }

    // ---- workspace-root resolution -----------------------------------------------------------------------------------

    /// <summary>The foreign workspace root owning the message's file, or null if it's a home file or carries no uri.</summary>
    private string? ResolveForeignRoot(LspMessage msg)
    {
        if (!_multiRepo) return null; // kill-switch: everything stays on the primary
        string? uri = msg.Json?["params"]?["textDocument"]?["uri"]?.GetValue<string>();
        if (uri is null) return null;
        return _uriRoot.GetOrAdd(uri, u => ComputeForeignRoot(u));
    }

    private string? ComputeForeignRoot(string uri)
    {
        string path;
        try { path = LspEdits.UriToPath(uri); } catch { return null; }
        if (string.IsNullOrEmpty(path)) return null;

        string full;
        try { full = Path.GetFullPath(path); } catch { return null; }
        if (Normalize(full).StartsWith(_homeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null; // under the home root → the home daemon's solution covers it

        // 🔑 ONE RULE, ONE PLACE. This walk used to live here as a SECOND implementation that disagreed with the
        // client's: it tested `Directory.Exists(".git")`, so a WORKTREE — whose .git is a FILE — was invisible to it
        // and the walk climbed straight past the worktree root. PipeKey is where the key is derived and therefore
        // where the root must be decided; a routing key computed by different code than the connecting key is two
        // answers to one question, which is exactly what the file's own summary promises cannot happen.
        //
        // 🩸 MEASURED 2026-09-08, self-inflicted and visible in the process table: editing two files in two
        // subdirectories of ONE marker-less tree (the plugin's own cache) spawned TWO daemons, rooted at
        // `.../0.1.1/bridge` and `.../0.1.1/client` — the old fallback below treated each file's own folder as a
        // workspace, so the daemon count grew with the number of directories touched.
        // NULL when nothing above the file is a repository — and null already means "the home daemon covers it" (see
        // the home-root return above). That is the whole cure for the daemon-per-folder growth: a file with no project
        // gets single-file analysis from ANY daemon, so the home one serves it and no second Roslyn is minted.
        return PipeKey.FindRepositoryRoot(full);
    }

    private static string Normalize(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    /// <summary>A per-repo secondary daemon link: connects asynchronously, queueing messages until it's ready so a file's
    /// didOpen and its edits all reach the secondary in order (never split across the spin-up).</summary>
    internal sealed class Secondary
    {
        public string Root { get; }
        private readonly ConcurrentQueue<byte[]> _pending = new();
        private readonly object _gate = new();
        private IFrameChannel? _channel;
        private bool _ready;
        private bool _connecting;

        public Secondary(string root) => Root = root;

        public IFrameChannel? Channel => _channel;

        public void Send(byte[] raw, DaemonRouter router)
        {
            IFrameChannel? ch;
            lock (_gate)
            {
                if (_ready) { ch = _channel; }
                else
                {
                    _pending.Enqueue(raw);
                    bool kick = !_connecting; _connecting = true;
                    if (kick) _ = router.ConnectSecondaryAsync(this);
                    return;
                }
            }
            if (ch is not null) router.WriteTo(ch, raw);
        }

        public void MarkReady(IFrameChannel channel, DaemonRouter router) => Flush(channel, router);

        // On connect failure, drain to the primary so the file still gets (loose) analysis rather than vanishing.
        public void FallBack(IFrameChannel primary, DaemonRouter router) => Flush(primary, router);

        private void Flush(IFrameChannel target, DaemonRouter router)
        {
            lock (_gate) { _channel = target; _ready = true; }
            while (_pending.TryDequeue(out byte[]? f)) router.WriteTo(target, f);
        }
    }
}
