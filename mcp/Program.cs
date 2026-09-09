using ClaudeRoslynLsp.Bridge;
using ClaudeRoslynLsp.Extensions;
using ClaudeRoslynLsp.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// No MSBuild registration any more: the refactor tools drive the SHARED Roslyn daemon over Sluice (DaemonSession) rather
// than loading an in-process MSBuildWorkspace, so this process holds no Roslyn workspace and no MSBuild at all.
var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP JSON-RPC wire — ALL logging must go to stderr or it corrupts the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
void Log(string m) => Console.Error.WriteLine($"[roslyn-mcp] {m}");

builder.Services.AddSingleton<DaemonSession>();

// Load repo-declared extensions (.claude-roslyn/extensions.json) — system-specific tools that give the agent visibility
// into a RUNNING SYSTEM (the extension dials out to it). Each IMcpToolExtension registers its services (e.g. a client to
// that system) and its [McpServerToolType] classes, which we add alongside the built-in refactor tools. The open-source
// MCP stays system-agnostic; everything system-specific lives in the loaded extension and its manifest config.
// 🩸 THE LAUNCH RECORD EXISTED AND THIS PATH NEVER WROTE IT. client-launch.log — pid, cwd, args, with DATES — is
// the one record that answers "which root did each client resolve", and five agents spent an evening reconstructing
// that from a process table because it stops at 2026-09-08 15:21.
//
// ⚖️ AND THE DIAGNOSIS WAS NOT WHAT IT LOOKED LIKE. @blackmagic read it as "silently dead behind a catch that
// swallows". Measured: client.log, IN THE SAME DIRECTORY, was last written 2026-09-09 01:53 — the directory is
// writable and the LSP client's logger still works. The launch log stopped because THE PATH THAT WRITES IT STOPPED
// RUNNING: only client/lsp-client.cs ever wrote it, and since 15:21 clients are launched through THIS process,
// which never wrote one. Not a failing write — an absent one, which is why no error appeared anywhere.
//
// 🔑 IT LOGS THE RESOLVED ROOT, NOT THE CWD, because the resolved root is the question. A record saying where the
// process started cannot answer which daemon it keyed to, and the gap between those two is the entire defect this
// fleet chased tonight.
try
{
    string launchDir = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA") is { Length: > 0 } pdir
        ? Path.Combine(pdir, "roslyn")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-roslyn-lsp");
    Directory.CreateDirectory(launchDir);
    string cwdNow = Directory.GetCurrentDirectory();
    string envRoot = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT") ?? "";
    string resolved = envRoot.Length > 0 ? envRoot : PipeKey.ResolveWorkspaceRoot(cwdNow);
    File.AppendAllText(Path.Combine(launchDir, "client-launch.log"),
        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] launched mcp pid={Environment.ProcessId} cwd={cwdNow} "
        + $"root={resolved}{(envRoot.Length > 0 ? " (CLAUDE_ROSLYN_WORKSPACE_ROOT, verbatim)" : resolved == cwdNow ? "" : " (walked up from cwd)")} "
        + $"endpoint={PipeKey.ForRoot(resolved)} args=[{string.Join(' ', args)}]{Environment.NewLine}");
}
catch (Exception ex)
{
    // NOT SILENT. A best-effort log whose failure is invisible is indistinguishable from a path that never ran —
    // which is exactly the ambiguity that cost this fleet an evening. stderr is the MCP host's own channel.
    Console.Error.WriteLine($"[roslyn-mcp] could not write the launch record: {ex.Message}");
}

string workspaceRoot = Environment.GetEnvironmentVariable("CLAUDE_ROSLYN_WORKSPACE_ROOT") ?? Directory.GetCurrentDirectory();
IReadOnlyList<LoadedExtension> extensions = ExtensionLoader.Load(workspaceRoot, "mcp", Log);
var extensionToolAssemblies = new List<System.Reflection.Assembly>();
foreach (LoadedExtension le in extensions)
{
    if (le.Extension is IMcpToolExtension mcpExt)
    {
        try
        {
            mcpExt.ConfigureServices(builder.Services);
            if (mcpExt.ToolTypes.Any() && !extensionToolAssemblies.Contains(le.Assembly)) extensionToolAssemblies.Add(le.Assembly);
            Log($"extension '{le.Name}' contributed {mcpExt.ToolTypes.Count()} MCP tool type(s)");
        }
        catch (Exception ex) { Log($"extension '{le.Name}' ConfigureServices failed: {ex.Message}"); }
    }
}
builder.Services.AddSingleton(extensions);
builder.Services.AddSingleton(new ExtensionWorkspace(workspaceRoot));
builder.Services.AddHostedService<ExtensionLifecycle>();

IMcpServerBuilder mcp = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
// Discover each extension's [McpServerToolType] classes the same way the host discovers its own (WithToolsFromAssembly) —
// the assembly scan reliably surfaces tool methods on externally-loaded types (shared ModelContextProtocol identity).
foreach (System.Reflection.Assembly asm in extensionToolAssemblies) mcp.WithToolsFromAssembly(asm);

await builder.Build().RunAsync();
