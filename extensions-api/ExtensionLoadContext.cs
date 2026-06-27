using System.Reflection;
using System.Runtime.Loader;

namespace ClaudeRoslynLsp.Extensions;

/// <summary>
/// A collectible load context for ONE extension, so a rebuilt extension can be swapped in at runtime (the old context is
/// unloaded, a new one loads the new dll) without restarting the host process.
///
/// The pivot is what stays SHARED with the host vs what loads privately here. The contract types (this assembly's
/// interfaces, <c>IServiceCollection</c>, the MCP attributes) MUST be the host's copies — otherwise the host's
/// <c>is IDiagnosticExtension</c> / <c>[McpServerToolType]</c> checks fail across a load-context boundary. So <see cref="Load"/>
/// returns <c>null</c> for those (deferring to the default context, where the host already has them) and loads only the
/// extension's OWN assembly + its private dependencies here (resolved from the extension's <c>.deps.json</c>).
/// </summary>
internal sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ExtensionLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Shared contract + framework → resolve from the DEFAULT context so types are identity-equal to the host's.
        if (IsHostShared(assemblyName.Name)) return null;
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>Assemblies whose types must be the host's single copy (shared identity), not a per-extension reload.</summary>
    private static bool IsHostShared(string? n) =>
        n is null
        || n == "ClaudeRoslynLsp.Extensions.Abstractions"          // the contract the host and extension both speak
        || n.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) // IServiceCollection
        || n == "ModelContextProtocol" || n.StartsWith("ModelContextProtocol.", StringComparison.Ordinal) // MCP tool attrs (MCP host)
        || n == "System" || n.StartsWith("System.", StringComparison.Ordinal)
        || n.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
        || n.StartsWith("Microsoft.Bcl.", StringComparison.Ordinal)
        || n == "netstandard" || n == "mscorlib" || n == "WindowsBase";
}
