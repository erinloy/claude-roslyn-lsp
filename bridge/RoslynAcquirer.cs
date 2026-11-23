using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace ClaudeRoslynLsp.Bridge;

/// <summary>
/// Ensures Microsoft's Roslyn language server (<c>Microsoft.CodeAnalysis.LanguageServer.&lt;rid&gt;</c>) is present on the
/// device, downloading + extracting the per-platform NuGet package from the vs-impl feed on first run and caching it under the
/// plugin data dir. This is the same server the C# Dev Kit runs; acquiring it (rather than shipping a binary) keeps the
/// plugin source-only and lets it pick up the latest published build.
/// </summary>
internal static class RoslynAcquirer
{
    public const string VersionEnvVar = "CLAUDE_ROSLYN_VERSION";       // pin an exact server version
    public const string ServerPathEnvVar = "CLAUDE_ROSLYN_SERVER_PATH"; // point at an already-extracted server dll, skip download
    private const string IdBase = "Microsoft.CodeAnalysis.LanguageServer";
    private const string ServerDll = "Microsoft.CodeAnalysis.LanguageServer.dll";
    // Roslyn publishes the server to the public vs-impl feed; nuget.org holds one build, 5.0.0-1.25277.114 (fyre 11-23: vs-impl had 5.4.0-2.26179.14).
    private const string FeedIndex = "https://pkgs.dev.azure.com/azure-public/vside/_packaging/vs-impl/nuget/v3/index.json";

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
        log($"downloading Roslyn server {id} {version} from the vs-impl feed …");
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

    /// <summary>The feed's flat-container address, read from its service index (the address carries feed ids, not the feed's name).</summary>
    private static async Task<string> PackageBaseAsync(HttpClient http, CancellationToken ct)
    {
        string json = await http.GetStringAsync(FeedIndex, ct).ConfigureAwait(false);
        string? at = JsonNode.Parse(json)?["resources"]?.AsArray()
            .FirstOrDefault(r => r?["@type"]?.GetValue<string>().StartsWith("PackageBaseAddress", StringComparison.Ordinal) == true)?["@id"]?.GetValue<string>();
        return at ?? throw new InvalidOperationException($"{FeedIndex} names no PackageBaseAddress");
    }

    private static async Task<string> ResolveLatestVersionAsync(string idLower, CancellationToken ct)
    {
        using var http = NewHttp();
        // NuGet flat-container version index — versions ascending; the last is the newest published (incl. prerelease).
        string url = $"{await PackageBaseAsync(http, ct).ConfigureAwait(false)}{idLower}/index.json";
        string json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        var versions = JsonNode.Parse(json)?["versions"]?.AsArray().Select(v => v!.GetValue<string>()).ToList();
        if (versions is null || versions.Count == 0)
            throw new InvalidOperationException($"no versions listed for {idLower}");
        // the feed lists versions in publish order, not version order: its last entry was a 4.8.0 build
        return versions.MaxBy(SemVerKey.Of)!;
    }

    private static async Task DownloadAndExtractAsync(string idLower, string version, string versionDir, CancellationToken ct)
    {
        using var http = NewHttp();
        string url = $"{await PackageBaseAsync(http, ct).ConfigureAwait(false)}{idLower}/{version}/{idLower}.{version}.nupkg";
        // The download lands BESIDE its destination, inside the plugin data root — never Path.GetTempPath(), which on
        // Windows is %LOCALAPPDATA%\Temp (Erin, 2026-09-17: no plugin state in AppData). Same directory as the
        // extraction also means the same volume, and the finally below removes it whether or not extraction succeeds.
        string tmp = $"{versionDir}.{Guid.NewGuid():N}.nupkg";
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

/// <summary>SemVer 2 precedence: numeric core, then a release above any prerelease, then prerelease identifiers in turn (a
/// numeric one below an alphanumeric one, numerics by value).</summary>
internal readonly record struct SemVerKey(int[] Core, string[]? Pre) : IComparable<SemVerKey>
{
    public static SemVerKey Of(string v)
    {
        string bare = v.Split('+')[0];
        int dash = bare.IndexOf('-');
        string core = dash < 0 ? bare : bare[..dash];
        return new(core.Split('.').Select(int.Parse).ToArray(), dash < 0 ? null : bare[(dash + 1)..].Split('.'));
    }

    public int CompareTo(SemVerKey other)
    {
        for (int i = 0; i < Math.Max(Core.Length, other.Core.Length); i++)
        {
            int c = (i < Core.Length ? Core[i] : 0).CompareTo(i < other.Core.Length ? other.Core[i] : 0);
            if (c != 0) return c;
        }
        if (Pre is null || other.Pre is null) return (Pre is null).CompareTo(other.Pre is null);
        for (int i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            bool an = long.TryParse(Pre[i], out long a), bn = long.TryParse(other.Pre[i], out long b);
            int c = an && bn ? a.CompareTo(b) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(Pre[i], other.Pre[i]);
            if (c != 0) return c;
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }
}
