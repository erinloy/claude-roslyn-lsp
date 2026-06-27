using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// Ensures Microsoft's Roslyn language server (<c>Microsoft.CodeAnalysis.LanguageServer.&lt;rid&gt;</c>) is present on the
/// device, downloading + extracting the per-platform NuGet package from nuget.org on first run and caching it under the
/// plugin data dir. This is the same server the C# Dev Kit runs; acquiring it (rather than shipping a binary) keeps the
/// plugin source-only and lets it pick up the latest published build.
/// </summary>
internal static class RoslynAcquirer
{
    public const string VersionEnvVar = "CLAUDE_ROSLYN_VERSION";       // pin an exact server version
    public const string ServerPathEnvVar = "CLAUDE_ROSLYN_SERVER_PATH"; // point at an already-extracted server dll, skip download
    private const string IdBase = "Microsoft.CodeAnalysis.LanguageServer";
    private const string ServerDll = "Microsoft.CodeAnalysis.LanguageServer.dll";

    /// <summary>Returns the absolute path to the Roslyn server dll, acquiring it if necessary.</summary>
    public static async Task<string> EnsureServerAsync(string dataDir, Action<string> log, CancellationToken ct)
    {
        // 0. Explicit override — a pre-provisioned server dll.
        string? overridePath = Environment.GetEnvironmentVariable(ServerPathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            log($"using {ServerPathEnvVar}={overridePath}");
            return overridePath;
        }

        string rid = Rid();
        string id = $"{IdBase}.{rid}";
        string idLower = id.ToLowerInvariant();
        string serverRoot = Path.Combine(dataDir, "server", rid);
        Directory.CreateDirectory(serverRoot);

        // 1. If a cached extraction already has the server dll, reuse it (offline-friendly; the newest cached wins).
        string? cached = FindNewestCachedServer(serverRoot);

        // 2. Resolve the desired version (pin via env, else latest from nuget.org). Network failure is non-fatal when a
        //    cached copy exists.
        string? version = Environment.GetEnvironmentVariable(VersionEnvVar);
        if (string.IsNullOrWhiteSpace(version))
        {
            try { version = await ResolveLatestVersionAsync(idLower, ct).ConfigureAwait(false); }
            catch (Exception ex) when (cached is not null)
            {
                log($"version lookup failed ({ex.Message}); using cached server {cached}");
                return cached;
            }
        }

        string versionDir = Path.Combine(serverRoot, version!);
        string? extracted = FindServerDll(versionDir);
        if (extracted is not null)
        {
            log($"roslyn server ready (cached {rid} {version})");
            return extracted;
        }

        // 3. Download + extract the nupkg (a zip) into the versioned dir.
        log($"downloading Roslyn server {id} {version} from nuget.org …");
        try
        {
            await DownloadAndExtractAsync(idLower, version!, versionDir, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (cached is not null)
        {
            log($"download failed ({ex.Message}); falling back to cached server {cached}");
            return cached;
        }

        extracted = FindServerDll(versionDir)
            ?? throw new FileNotFoundException($"{ServerDll} not found after extracting {id} {version}");
        log($"roslyn server installed: {extracted}");
        return extracted;
    }

    private static async Task<string> ResolveLatestVersionAsync(string idLower, CancellationToken ct)
    {
        using var http = NewHttp();
        // NuGet flat-container version index — versions ascending; the last is the newest published (incl. prerelease).
        string url = $"https://api.nuget.org/v3-flatcontainer/{idLower}/index.json";
        string json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        var versions = JsonNode.Parse(json)?["versions"]?.AsArray();
        if (versions is null || versions.Count == 0)
            throw new InvalidOperationException($"no versions listed for {idLower}");
        return versions[^1]!.GetValue<string>();
    }

    private static async Task DownloadAndExtractAsync(string idLower, string version, string versionDir, CancellationToken ct)
    {
        using var http = NewHttp();
        string url = $"https://api.nuget.org/v3-flatcontainer/{idLower}/{version}/{idLower}.{version}.nupkg";
        string tmp = Path.Combine(Path.GetTempPath(), $"{idLower}.{version}.{Guid.NewGuid():N}.nupkg");
        try
        {
            await using (var resp = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
            await using (var file = File.Create(tmp))
                await resp.CopyToAsync(file, ct).ConfigureAwait(false);

            string staging = versionDir + ".staging";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ZipFile.ExtractToDirectory(tmp, staging);

            // Atomic-ish publish: only move into place once extraction succeeded, so a crash never leaves a half dir.
            if (Directory.Exists(versionDir)) Directory.Delete(versionDir, recursive: true);
            Directory.Move(staging, versionDir);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The RUNNABLE server lives under <c>content/LanguageServer/&lt;rid|neutral&gt;/</c> (with its native apphost
    /// alongside). The package ALSO carries a <c>lib/&lt;tfm&gt;/</c> reference-assembly copy of the same file name — that
    /// one is not runnable, so prefer the content path (then any dir that has the apphost) and glob to stay version-proof.
    /// </summary>
    private static string? FindServerDll(string root)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            var all = Directory.EnumerateFiles(root, ServerDll, SearchOption.AllDirectories).ToList();
            if (all.Count == 0) return null;
            return all.FirstOrDefault(p => p.Replace('\\', '/').Contains("/content/LanguageServer/", StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(HasAppHost)
                ?? all[0];
        }
        catch { return null; }
    }

    private static bool HasAppHost(string dll)
    {
        string dir = Path.GetDirectoryName(dll)!;
        string host = Path.Combine(dir,
            OperatingSystem.IsWindows() ? "Microsoft.CodeAnalysis.LanguageServer.exe" : "Microsoft.CodeAnalysis.LanguageServer");
        return File.Exists(host);
    }

    private static string? FindNewestCachedServer(string serverRoot)
    {
        if (!Directory.Exists(serverRoot)) return null;
        return Directory.EnumerateDirectories(serverRoot)
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(FindServerDll)
            .FirstOrDefault(p => p is not null);
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("claude-roslyn-lsp/0.1");
        return http;
    }

    private static string Rid()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }
}
