using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// One framed LSP message: the raw bytes (exactly as read, ready to forward verbatim) plus a lazily-parsed JSON view
/// used only to peek at <c>method</c> / params for the handful of messages the bridge intercepts.
/// </summary>
internal sealed class LspMessage
{
    public required byte[] Raw { get; init; }
    private JsonNode? _parsed;
    private bool _parseAttempted;

    public JsonNode? Json
    {
        get
        {
            if (!_parseAttempted)
            {
                _parseAttempted = true;
                try { _parsed = JsonNode.Parse(Raw); }
                catch { _parsed = null; }
            }
            return _parsed;
        }
    }

    public string? Method => Json?["method"]?.GetValue<string>();
}

/// <summary>
/// Reads <c>Content-Length</c>-framed LSP messages from a stream (the JSON-RPC base protocol: an ASCII header block
/// terminated by a blank line, then exactly Content-Length bytes of UTF-8 body). Returns each message's raw bytes so
/// the proxy can forward them byte-identically.
/// </summary>
internal sealed class LspMessageReader(Stream stream)
{
    private readonly Stream _stream = stream;
    private readonly List<byte> _headerBuf = new(256);

    public async Task<LspMessage?> ReadAsync(CancellationToken ct)
    {
        int contentLength = await ReadHeadersAsync(ct).ConfigureAwait(false);
        if (contentLength < 0) return null; // EOF

        var body = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = await _stream.ReadAsync(body.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
            if (n == 0) return null; // EOF mid-body
            read += n;
        }
        return new LspMessage { Raw = body };
    }

    /// <summary>Reads the header block; returns the Content-Length, or -1 on clean EOF.</summary>
    private async Task<int> ReadHeadersAsync(CancellationToken ct)
    {
        _headerBuf.Clear();
        int contentLength = -1;
        var one = new byte[1];

        while (true)
        {
            int n = await _stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return _headerBuf.Count == 0 ? -1 : throw new EndOfStreamException("EOF inside header block");
            byte b = one[0];
            _headerBuf.Add(b);

            // End of a header line?
            int c = _headerBuf.Count;
            if (b == (byte)'\n' && c >= 2 && _headerBuf[c - 2] == (byte)'\r')
            {
                // A bare CRLF terminates the header block.
                if (c == 2) return contentLength;

                string line = Encoding.ASCII.GetString(CollectionsMarshalSpan(_headerBuf, 0, c - 2));
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string name = line[..colon].Trim();
                    string value = line[(colon + 1)..].Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(value, out int len))
                    {
                        contentLength = len;
                    }
                }
                _headerBuf.Clear();
            }
        }
    }

    private static ReadOnlySpan<byte> CollectionsMarshalSpan(List<byte> list, int start, int length)
        => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).Slice(start, length);
}

/// <summary>
/// Writes framed LSP messages to a stream. A single writer instance must serialize its writes (one pump owns it), so
/// the bridge's injected notifications and the forwarded client traffic never interleave mid-frame.
/// </summary>
internal sealed class LspMessageWriter(Stream stream)
{
    private readonly Stream _stream = stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteRawAsync(ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            await _stream.WriteAsync(body, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task WriteJsonAsync(JsonNode node, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        return WriteRawAsync(bytes, ct);
    }
}
