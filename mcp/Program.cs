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
