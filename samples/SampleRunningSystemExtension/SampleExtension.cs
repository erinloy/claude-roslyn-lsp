using System.ComponentModel;
using System.Diagnostics;
using ClaudeRoslynLsp.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace SampleRunningSystemExtension;

/// <summary>
/// Reference extension showing the full shape of a claude-roslyn-lsp extension: it contributes an MCP tool that reports
/// LIVE state from a running system. Here the "running system" is just this host process (so the sample is self-contained
/// and needs nothing external) — a real extension dials OUT to the actual system named in its manifest <c>config</c>
/// (e.g. Matrix's HTTP API) and reports that instead.
///
/// Manifest (<c>.claude-roslyn/extensions.json</c>):
/// <code>
/// { "extensions": [ {
///     "name": "sample",
///     "assembly": "samples/SampleRunningSystemExtension/bin/Release/net8.0/SampleRunningSystemExtension.dll",
///     "type": "SampleRunningSystemExtension.SampleExtension",
///     "config": { "endpoint": "self" }
/// } ] }
/// </code>
/// </summary>
public sealed class SampleExtension : ICrlspExtension, IMcpToolExtension
{
    public string Name => "sample-running-system";

    private readonly SampleSystemClient _client = new();

    public Task InitializeAsync(ExtensionContext context, CancellationToken ct)
    {
        string endpoint = context.Config?["endpoint"]?.GetValue<string>() ?? "self";
        context.Log($"attaching to running system '{endpoint}' (host={context.Host}, root={context.WorkspaceRoot})");
        _client.Attach(endpoint, context.Log);
        return Task.CompletedTask;
    }

    // The tool classes inject SampleSystemClient — register it so the MCP DI container can construct them.
    public void ConfigureServices(IServiceCollection services) => services.AddSingleton(_client);

    public IEnumerable<Type> ToolTypes => new[] { typeof(SampleTools) };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Stands in for the connection to the running system. A real extension would hold an HTTP/Sluice/pipe client.</summary>
public sealed class SampleSystemClient
{
    private string _endpoint = "self";

    public void Attach(string endpoint, Action<string> log)
    {
        _endpoint = endpoint;
        log("attached (sample reports the MCP host process itself as the running system)");
    }

    public string Status()
    {
        Process p = Process.GetCurrentProcess();
        TimeSpan up = DateTime.Now - p.StartTime;
        return $"running-system '{_endpoint}' status:\n" +
               $"  host pid:     {p.Id}\n" +
               $"  uptime:       {up:hh\\:mm\\:ss}\n" +
               $"  working set:  {p.WorkingSet64 / 1024 / 1024} MB\n" +
               $"  threads:      {p.Threads.Count}";
    }
}

[McpServerToolType]
public sealed class SampleTools
{
    private readonly SampleSystemClient _client;

    public SampleTools(SampleSystemClient client) => _client = client;

    [McpServerTool(Name = "running_system_status")]
    [Description("Report live status from the running system this extension is attached to. Demonstrates agent visibility " +
                 "into a RUNNING SYSTEM contributed by a repo-loaded extension (not the static codebase). The sample " +
                 "reports the MCP host process's own live stats; a real extension reports the system it dials out to.")]
    public string RunningSystemStatus() => _client.Status();
}
