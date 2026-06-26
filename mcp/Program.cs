using ClaudeRoslynLsp.Mcp;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MSBuild MUST be registered before any Microsoft.CodeAnalysis.*.MSBuild type is loaded/JITed, so do it first thing.
if (!MSBuildLocator.IsRegistered)
{
    try { MSBuildLocator.RegisterDefaults(); }
    catch (Exception ex) { Console.Error.WriteLine($"[mcp] MSBuild registration failed: {ex.Message}"); }
}

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP JSON-RPC wire — ALL logging must go to stderr or it corrupts the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<WorkspaceHost>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
