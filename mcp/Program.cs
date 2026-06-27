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

builder.Services.AddSingleton<DaemonSession>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
