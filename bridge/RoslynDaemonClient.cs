using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Sluice;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// A request/response LSP client over a connected daemon frame channel. Unlike the relay client (which just pumps
/// Claude's stdio), this issues its OWN LSP requests — references / rename / formatting / workspace-symbol — and awaits
/// the correlated responses. It is how the refactor MCP drives the ONE shared Roslyn workspace instead of loading its
/// own MSBuildWorkspace (N agents × a full solution → 1× shared). The reusable broker's client half.
/// </summary>
public sealed class RoslynDaemonClient : IAsyncDisposable
{
    private readonly IFrameChannel _channel;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly object _writeGate = new();   // ShmRing is single-producer per direction — serialise our writes
    private long _nextId;

    private RoslynDaemonClient(IFrameChannel channel) => _channel = channel;

    /// <summary>Connect to the workspace daemon (starting it if needed) and complete the LSP initialize handshake.</summary>
    public static async Task<RoslynDaemonClient?> ConnectAsync(
        string root, string? solution, string pluginRoot, Action<string> log, CancellationToken ct = default)
    {
        string endpoint = PipeKey.ForRoot(root);
        IFrameChannel? ch = await DaemonConnector.ConnectAsync(endpoint, root, solution, pluginRoot, log).ConfigureAwait(false);
        if (ch is null) return null;

        var c = new RoslynDaemonClient(ch);
        // Announce our PID so the daemon reaps us when we exit (shared memory has no peer-EOF), then start routing.
        c.WriteFrame(Encoding.UTF8.GetBytes($"{{\"{PipeKey.PidHelloKey}\":{Environment.ProcessId}}}"));
        new Thread(c.ReadLoop) { IsBackground = true, Name = "crlsp-daemonclient-rd" }.Start();

        // The daemon answers initialize from its cached result; initialized is swallowed. The workspace is already open.
        await c.RequestAsync("initialize",
            new JsonObject { ["processId"] = Environment.ProcessId, ["rootUri"] = PathToUri(root), ["capabilities"] = new JsonObject() }, ct)
            .ConfigureAwait(false);
        c.Notify("initialized", new JsonObject());
        return c;
    }

    /// <summary>Send an LSP request and await its correlated result (null result is a valid LSP response).</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? @params, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        WriteFrame(Encoding.UTF8.GetBytes(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }.ToJsonString()));

        // Fail the await if the caller cancels or the channel closes, so a tool call can't hang forever.
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        using (_closed.Token.Register(() => tcs.TrySetException(new IOException("daemon channel closed"))))
        {
            try { return await tcs.Task.ConfigureAwait(false); }
            finally { _pending.TryRemove(id, out _); }
        }
    }

    public void Notify(string method, JsonNode? @params)
        => WriteFrame(Encoding.UTF8.GetBytes(
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params }.ToJsonString()));

    private void WriteFrame(byte[] frame) { lock (_writeGate) { try { _channel.WriteFrame(frame, _closed.Token); } catch { } } }

    private void ReadLoop()
    {
        try
        {
            while (!_closed.IsCancellationRequested && _channel.WaitForFrame(_closed.Token))
            {
                while (_channel.TryReadFrame(out var span))
                {
                    byte[] raw = span.ToArray();
                    _channel.AdvanceFrame();
                    JsonNode? node;
                    try { node = JsonNode.Parse(raw); } catch { continue; }
                    // A response carries an id and no method. Server→client requests are answered by the daemon itself;
                    // broadcast notifications (diagnostics) are irrelevant to a request-driven client — ignore both.
                    if (node?["id"] is { } idNode && node["method"] is null
                        && long.TryParse(idNode.ToString(), out long id) && _pending.TryRemove(id, out var tcs))
                        tcs.TrySetResult(node["result"]);
                }
            }
        }
        catch { }
        finally
        {
            _closed.Cancel();
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException("daemon channel closed"));
        }
    }

    private static string PathToUri(string path)
    {
        try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; } catch { return path; }
    }

    public async ValueTask DisposeAsync()
    {
        try { Notify("exit", null); } catch { }
        _closed.Cancel();
        try { _channel.Dispose(); } catch { }
        await Task.CompletedTask;
    }
}
