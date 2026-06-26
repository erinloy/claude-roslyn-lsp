using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// The bridge proper. Spawns the Roslyn language server and shuttles LSP traffic between this process's stdio (the
/// Claude Code client) and the server's stdio, transparently — except for the one thing the server needs that a generic
/// client never sends: after the client's <c>initialized</c>, the bridge injects the Roslyn-specific <c>solution/open</c>
/// (or <c>project/open</c>) notification so the server actually loads the workspace. Everything else passes through
/// byte-for-byte, so all of Roslyn's features (diagnostics, code actions, rename, hover, call hierarchy, …) work.
/// </summary>
internal sealed class LspStdioProxy
{
    private readonly string _serverDll;
    private readonly string _logDir;
    private readonly string? _solutionOverride;
    private readonly Action<string> _log;

    private string? _rootPath;

    public LspStdioProxy(string serverDll, string logDir, string? solutionOverride, Action<string> log)
    {
        _serverDll = serverDll;
        _logDir = logDir;
        _solutionOverride = solutionOverride;
        _log = log;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var server = StartServer();
        _log($"roslyn server pid {server.Id} started");

        var clientIn = Console.OpenStandardInput();
        var clientOut = Console.OpenStandardOutput();
        var serverIn = server.StandardInput.BaseStream;
        var serverOut = server.StandardOutput.BaseStream;

        var serverWriter = new LspMessageWriter(serverIn);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        // client → server: parse frames so we can capture the root (initialize) and inject solution/open (after initialized).
        Task pumpToServer = PumpClientToServerAsync(new LspMessageReader(clientIn), serverWriter, token);
        // server → client: opaque byte copy — nothing to inspect, just forward verbatim.
        Task pumpToClient = serverOut.CopyToAsync(clientOut, token).ContinueWith(_ => { }, TaskScheduler.Default);

        var exited = WaitForExitAsync(server, token);
        await Task.WhenAny(pumpToServer, pumpToClient, exited).ConfigureAwait(false);

        linked.Cancel();
        try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        return server.HasExited ? server.ExitCode : 0;
    }

    private Process StartServer()
    {
        Directory.CreateDirectory(_logDir);
        var (fileName, leadingArgs) = ResolveLauncher(_serverDll);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in leadingArgs) psi.ArgumentList.Add(a);
        // Roslyn server CLI: stdio transport + a required log directory. logLevel keeps the noise reasonable.
        psi.ArgumentList.Add("--stdio");
        psi.ArgumentList.Add("--logLevel");
        psi.ArgumentList.Add("Information");
        psi.ArgumentList.Add("--extensionLogDirectory");
        psi.ArgumentList.Add(_logDir);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log($"[roslyn] {e.Data}"); };
        proc.Start();
        proc.BeginErrorReadLine();
        return proc;
    }

    /// <summary>
    /// The <c>.&lt;rid&gt;</c> server package is self-contained: run its native apphost directly when present; otherwise
    /// fall back to <c>dotnet &lt;dll&gt;</c> (framework-dependent / neutral package).
    /// </summary>
    private static (string fileName, string[] leadingArgs) ResolveLauncher(string serverDll)
    {
        string dir = Path.GetDirectoryName(serverDll)!;
        string apphost = Path.Combine(dir,
            OperatingSystem.IsWindows() ? "Microsoft.CodeAnalysis.LanguageServer.exe" : "Microsoft.CodeAnalysis.LanguageServer");
        if (File.Exists(apphost)) return (apphost, Array.Empty<string>());
        return ("dotnet", new[] { serverDll });
    }

    private async Task PumpClientToServerAsync(LspMessageReader reader, LspMessageWriter serverWriter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            LspMessage? msg = await reader.ReadAsync(ct).ConfigureAwait(false);
            if (msg is null) return; // client closed

            string? method = SafeMethod(msg);
            if (method == "initialize") CaptureRoot(msg);

            await serverWriter.WriteRawAsync(msg.Raw, ct).ConfigureAwait(false);

            if (method == "initialized")
                await OpenWorkspaceAsync(serverWriter, ct).ConfigureAwait(false);
        }
    }

    private void CaptureRoot(LspMessage msg)
    {
        try
        {
            var p = msg.Json?["params"];
            string? uri = p?["rootUri"]?.GetValue<string>();
            if (uri is null)
            {
                var folders = p?["workspaceFolders"]?.AsArray();
                if (folders is { Count: > 0 }) uri = folders[0]?["uri"]?.GetValue<string>();
            }
            _rootPath = uri is not null ? UriToPath(uri) : p?["rootPath"]?.GetValue<string>();
            _log($"workspace root: {_rootPath ?? "(none)"}");
        }
        catch (Exception ex) { _log($"failed to read initialize params: {ex.Message}"); }
    }

    private async Task OpenWorkspaceAsync(LspMessageWriter serverWriter, CancellationToken ct)
    {
        if (_rootPath is null) { _log("no workspace root — not sending solution/open"); return; }

        WorkspaceTarget target = SolutionLocator.Locate(_rootPath, _solutionOverride, _log);
        if (!target.HasAny) return;

        JsonObject notification = target.SolutionPath is not null
            ? Notify("solution/open", new JsonObject { ["solution"] = PathToUri(target.SolutionPath) })
            : Notify("project/open", new JsonObject
            {
                ["projects"] = new JsonArray(target.ProjectPaths.Select(p => (JsonNode)PathToUri(p)!).ToArray())
            });

        await serverWriter.WriteJsonAsync(notification, ct).ConfigureAwait(false);
        _log(target.SolutionPath is not null
            ? $"sent solution/open → {target.SolutionPath}"
            : $"sent project/open → {target.ProjectPaths.Count} project(s)");
    }

    private static JsonObject Notify(string method, JsonObject @params) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params };

    private static string? SafeMethod(LspMessage msg)
    {
        try { return msg.Method; } catch { return null; }
    }

    private static async Task WaitForExitAsync(Process p, CancellationToken ct)
    {
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static string UriToPath(string uri)
    {
        try { return new Uri(uri).LocalPath; } catch { return uri; }
    }

    private static string PathToUri(string path)
    {
        try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; } catch { return path; }
    }
}
