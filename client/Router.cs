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
        StartDownPump(primary, filterInit: false, isPrimary: true); // primary relays everything incl. Claude's real init response
    }

    public void Dispose()
    {
        foreach (var kv in _secondaries)
            if (kv.Value.Channel is { } ch) { try { ch.Dispose(); } catch { } }
    }

    /// <summary>Route one Claude→daemon message to the daemon that owns its file (home or a per-repo secondary).</summary>
    public void Up(LspMessage msg)
    {
        string? foreignRoot = ResolveForeignRoot(msg);
        if (foreignRoot is null) { WriteTo(_primary, msg.Raw); return; }   // home file / no uri → primary (unchanged path)

        Secondary sec = _secondaries.GetOrAdd(foreignRoot, r => new Secondary(r));
        sec.Send(msg.Raw, this);
    }

    // ---- secondary lifecycle -----------------------------------------------------------------------------------------

    internal async Task ConnectSecondaryAsync(Secondary sec)
    {
        try
        {
            string endpoint = PipeKey.ForRoot(sec.Root);
            _log($"multi-repo: connecting secondary daemon for {sec.Root}");
            IFrameChannel? ch = await DaemonConnector.ConnectAsync(endpoint, sec.Root, null, _pluginRoot, _log).ConfigureAwait(false);
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
                        lock (_writeLock) { _writer.WriteRawAsync(frame, _ct).GetAwaiter().GetResult(); } // re-frame with Content-Length to Claude
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

        // Walk up to the nearest workspace marker; that repo gets its own daemon.
        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(Path.GetDirectoryName(full) ?? full); } catch { return null; }
        for (; dir is not null; dir = dir.Parent)
        {
            try
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    Directory.EnumerateFiles(dir.FullName, "*.sln").Any() ||
                    Directory.EnumerateFiles(dir.FullName, "*.slnx").Any())
                    return dir.FullName;
            }
            catch { /* unreadable dir — keep walking */ }
        }
        return Path.GetDirectoryName(full); // no marker found: treat the file's own folder as its root
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
