using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// Interrogates the Roslyn server for the capabilities/extensions it actually exposes at runtime — the answer to "what
/// can this server do?". Roslyn itself covers two LANGUAGES (C# and VB), but the set of CAPABILITIES it advertises
/// (code-action kinds, executable commands, semantic-token legend, the providers it lights up) is rich and version-
/// dependent, and it all comes back in the <c>initialize</c> result. This drives a minimal handshake, captures that
/// result, and prints a human summary (to stderr) plus the machine-readable capabilities JSON (to stdout).
/// </summary>
internal sealed class CapabilitiesProbe
{
    private readonly string _serverDll;
    private readonly string _logDir;
    private readonly string _rootPath;
    private readonly Action<string> _log;

    public CapabilitiesProbe(string serverDll, string logDir, string rootPath, Action<string> log)
    {
        _serverDll = serverDll;
        _logDir = logDir;
        _rootPath = rootPath;
        _log = log;
    }

    /// <summary>Runs the handshake and returns the server's <c>capabilities</c> object (null if it never arrived).</summary>
    public async Task<JsonObject?> ProbeAsync(CancellationToken ct)
    {
        using var server = RoslynServer.Start(_serverDll, _logDir, _log);
        _log($"roslyn server pid {server.Id} started (capabilities probe)");

        var writer = new LspMessageWriter(server.StandardInput.BaseStream);
        var reader = new LspMessageReader(server.StandardOutput.BaseStream);

        await writer.WriteJsonAsync(InitializeRequest(_rootPath), ct).ConfigureAwait(false);

        JsonObject? capabilities = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                LspMessage? msg = await reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (msg is null) break; // server closed
                JsonNode? json = msg.Json;
                if (json is null) continue;

                // The initialize RESULT (id == 1) carries the capabilities. Answer any server→client request that
                // arrives first (with null) so the server never blocks waiting on us.
                JsonNode? idNode = json["id"];
                bool isResponse = idNode is not null && json["method"] is null;
                if (isResponse && idNode!.GetValue<int>() == 1)
                {
                    capabilities = json["result"]?["capabilities"]?.AsObject();
                    break;
                }
                if (json["method"] is not null && idNode is not null)
                    await writer.WriteJsonAsync(AckRequest(idNode), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _log("capabilities probe timed out waiting for initialize result");
        }

        try { await writer.WriteJsonAsync(Notify("exit"), ct).ConfigureAwait(false); } catch { /* best-effort */ }
        try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        return capabilities;
    }

    /// <summary>Renders the capabilities as a readable summary (the things a user/agent cares about) + the raw JSON.</summary>
    public static string Summarize(JsonObject? capabilities)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Roslyn language server — runtime capabilities");
        sb.AppendLine();
        sb.AppendLine("Languages: C# (.cs, .csx), Visual Basic (.vb)");
        sb.AppendLine();

        if (capabilities is null)
        {
            sb.AppendLine("(no capabilities returned — the server did not complete the initialize handshake)");
            return sb.ToString();
        }

        // Providers present (a capability key present and not literally `false` means the feature is offered).
        var providers = capabilities
            .Where(kv => kv.Key.EndsWith("Provider", StringComparison.Ordinal))
            .Where(kv => kv.Value is not JsonValue v || !(v.TryGetValue(out bool b) && b == false))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        sb.AppendLine($"Providers ({providers.Count}):");
        foreach (var p in providers) sb.AppendLine($"  - {p}");
        sb.AppendLine();

        // Code-action kinds: the refactoring / quick-fix categories this server can produce.
        var codeActionKinds = capabilities["codeActionProvider"]?["codeActionKinds"]?.AsArray();
        if (codeActionKinds is { Count: > 0 })
        {
            sb.AppendLine($"Code-action kinds ({codeActionKinds.Count}):");
            foreach (var k in codeActionKinds) sb.AppendLine($"  - {k?.GetValue<string>()}");
            sb.AppendLine();
        }

        // Executable commands: the workspace/executeCommand verbs Roslyn registers (the extension surface).
        var commands = capabilities["executeCommandProvider"]?["commands"]?.AsArray();
        if (commands is { Count: > 0 })
        {
            sb.AppendLine($"Executable commands ({commands.Count}):");
            foreach (var c in commands) sb.AppendLine($"  - {c?.GetValue<string>()}");
            sb.AppendLine();
        }

        // Semantic-token legend: the token types/modifiers the server can classify.
        var tokenTypes = capabilities["semanticTokensProvider"]?["legend"]?["tokenTypes"]?.AsArray();
        if (tokenTypes is { Count: > 0 })
            sb.AppendLine($"Semantic token types: {tokenTypes.Count}");

        sb.AppendLine();
        sb.AppendLine("## Raw capabilities (machine-readable)");
        sb.AppendLine(capabilities.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return sb.ToString();
    }

    private static JsonObject InitializeRequest(string rootPath)
    {
        string rootUri = PathToUri(rootPath);
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = rootUri,
                ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = "probe" }),
                // Advertise broad client support so the server reports its full capability surface, not a reduced one.
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["configuration"] = true,
                        ["workspaceFolders"] = true,
                        ["executeCommand"] = new JsonObject { ["dynamicRegistration"] = true },
                    },
                    ["textDocument"] = new JsonObject
                    {
                        ["codeAction"] = new JsonObject
                        {
                            ["codeActionLiteralSupport"] = new JsonObject
                            {
                                ["codeActionKind"] = new JsonObject { ["valueSet"] = new JsonArray() },
                            },
                        },
                        ["rename"] = new JsonObject { ["prepareSupport"] = true },
                        ["semanticTokens"] = new JsonObject
                        {
                            ["requests"] = new JsonObject { ["full"] = true },
                            ["tokenTypes"] = new JsonArray(),
                            ["tokenModifiers"] = new JsonArray(),
                            ["formats"] = new JsonArray("relative"),
                        },
                    },
                },
            },
        };
    }

    private static JsonObject AckRequest(JsonNode id) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = null };

    private static JsonObject Notify(string method) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method };

    private static string PathToUri(string path)
    {
        try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; } catch { return path; }
    }
}
