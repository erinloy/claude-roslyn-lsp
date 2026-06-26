using System.Diagnostics;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// Spawns the Roslyn language server process over stdio. Shared by the stdio proxy (a long-lived session) and the
/// capabilities probe (a one-shot handshake), so both launch the server identically — native apphost when present,
/// else <c>dotnet &lt;dll&gt;</c>.
/// </summary>
internal static class RoslynServer
{
    public static Process Start(string serverDll, string logDir, Action<string> log)
    {
        Directory.CreateDirectory(logDir);
        var (fileName, leadingArgs) = ResolveLauncher(serverDll);

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
        psi.ArgumentList.Add(logDir);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log($"[roslyn] {e.Data}"); };
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
}
