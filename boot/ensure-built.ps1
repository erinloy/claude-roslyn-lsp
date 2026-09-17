<#
  ensure-built.ps1 — build the daemon, refactoring MCP, and LSP client to their Release DLLs ONCE, serialized across
  every Claude instance, skipping anything already fresh.

  Why this exists: the launch configs use `dotnet exec <dll>`, never `dotnet run`. `dotnet run` rebuilds on every
  launch, and N Claude instances launching the same shared cache copy concurrently race to build+lock the same output
  DLL → MSB3027 "The build failed" → the LSP/MCP server never connects. This script does the build up-front, under a
  machine-wide mutex, so exactly one instance builds while the rest wait; then every launch is a pure, instant exec.

  Run from the SessionStart hook (blocking) and re-runnable by hand:
      pwsh -NoProfile -File boot/ensure-built.ps1 -Root <plugin-root>
#>
param([string]$Root = "$PSScriptRoot\..")

$ErrorActionPreference = 'Continue'
$Root = (Resolve-Path $Root).Path

# THE ONE DATA-ROOT RULE (boot/data-root.ps1, twin of shared/PluginDataRoot.cs): CLAUDE_PLUGIN_DATA\roslyn, else
# ZILTCH_DATA_ROOT\claude-roslyn-lsp, else Z:\DATA\claude-roslyn-lsp. With none of them it THROWS, naming them, and this
# hook fails visibly at SessionStart — deliberately, since everything the plugin launches would fail on the same rule a
# moment later with a less direct message. It used to be %LOCALAPPDATA%; Erin, 2026-09-17: no plugin state in AppData.
. (Join-Path $PSScriptRoot 'data-root.ps1')
$logDir = Resolve-PluginDataRoot
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir 'ensure-built.log'
function L([string]$m) { "$([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff')) $m" | Out-File -FilePath $logFile -Append -Encoding utf8 }

# project name → (csproj, expected DLL). TFMs are pinned to each csproj's <TargetFramework>.
$projects = @(
    @{ name = 'daemon'; csproj = 'daemon\ClaudeRoslynLsp.Daemon.csproj'; dll = 'daemon\bin\Release\net8.0\ClaudeRoslynLsp.Daemon.dll' },
    @{ name = 'mcp';    csproj = 'mcp\ClaudeRoslynLsp.Mcp.csproj';       dll = 'mcp\bin\Release\net10.0\ClaudeRoslynLsp.Mcp.dll' },
    @{ name = 'client'; csproj = 'client\ClaudeRoslynLsp.Client.csproj'; dll = 'client\bin\Release\net8.0\ClaudeRoslynLsp.Client.dll' },
    @{ name = 'cli';    csproj = 'cli\ClaudeRoslynLsp.Cli.csproj';       dll = 'cli\bin\Release\net8.0\crlsp.dll' }
)

# --- Locate the vendored sibling libraries (Sluice, ConsoleAppFramework) ------------------------------------------
# The projects reference them relative to a normal CHECKOUT (../___/external). An INSTALLED plugin is a copy in Claude's
# plugin cache, where that path resolves to nothing and EVERY build fails with MSB9008 — silently, since this script only
# logged it. That froze the deployed binaries: the LSP kept running whatever was built the day the reference landed, so
# fixes made afterwards never reached it. Resolve the root here and hand it to MSBuild (Directory.Build.props reads it).
function Resolve-ExternalRoot([string]$root) {
    $candidates = @()
    if ($env:ZILTCH_EXTERNAL_ROOT) { $candidates += $env:ZILTCH_EXTERNAL_ROOT }
    $candidates += (Join-Path $root '..\___\external')                # normal checkout: the sibling repo
    # An installed copy has lost that context — recover it from the marketplace this plugin was installed FROM.
    $mk = Join-Path $env:USERPROFILE '.claude\plugins\known_marketplaces.json'
    if (Test-Path $mk) {
        try {
            $json = Get-Content $mk -Raw | ConvertFrom-Json
            foreach ($m in $json.PSObject.Properties.Value) {
                $src = $m.source
                if ($src -is [string]) { $dir = $src } elseif ($src.path) { $dir = $src.path } elseif ($src.source) { $dir = $src.source } else { continue }
                if ($dir -and (Test-Path $dir)) { $candidates += (Join-Path $dir '..\___\external') }
            }
        } catch { }
    }
    foreach ($c in $candidates) {
        try { $full = (Resolve-Path $c -ErrorAction Stop).Path } catch { continue }
        if (Test-Path (Join-Path $full 'Sluice\src\Sluice\Sluice.csproj')) { return $full }
    }
    return $null
}

$externalRoot = Resolve-ExternalRoot $Root
if ($externalRoot) { $env:ZILTCH_EXTERNAL_ROOT = $externalRoot; L "external root: $externalRoot" }
else { L "WARN: could not locate the vendored externals (Sluice) — builds will fail with MSB9008" }

# WHAT A PROJECT IS BUILT FROM, READ FROM ITS CSPROJ RATHER THAN ASSUMED: its own directory, every <Compile Include> it
# links from elsewhere (bridge/*.cs, shared/PluginDataRoot.cs), and — recursively — every in-plugin <ProjectReference>
# (extensions-api/). A $(ExternalRoot) reference (Sluice, ConsoleAppFramework) is vendored, not this plugin's source, and
# is not tracked.
#
# 🩸 THE OLD RULE WAS "own directory + ALL of bridge/", AND IT REBUILT ON EVERY SESSION START once any bridge/ file a
# project does not compile was edited (RoslynAcquirer.cs is not in the client or the cli; bridge's own csproj is in none).
# That file stays newer than the dll forever, because the build it triggers is a correct no-op that never rewrites the
# dll. Measured 2026-09-17: two consecutive runs with no source change between them each rebuilt daemon, mcp, client and
# cli. The opposite gap was open too: shared/ (new) and extensions-api/ were in no project's list at all.
#
# ⚖️ AND THE COMPARISON IS AGAINST THE NEWEST FILE IN THE OUTPUT DIRECTORY, NOT THE PRIMARY DLL — the same stamp run.ps1
# uses. A change inside a referenced project that leaves its public surface alone does not recompile the referencing
# project (its reference assembly is unchanged), so the primary dll keeps its old time while the referenced dll copied
# beside it is new. Against the primary dll alone that is the same forever-stale loop.
function Get-ProjectSources([string]$csproj, [hashtable]$seen) {
    $full = [System.IO.Path]::GetFullPath($csproj)
    if ($seen.ContainsKey($full)) { return @() }
    $seen[$full] = $true
    $dir = Split-Path $full -Parent
    $files = @(Get-Item -LiteralPath $full)
    $files += @(Get-ChildItem -Path $dir -Recurse -Include *.cs, *.csproj -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    [xml]$xml = Get-Content -LiteralPath $full -Raw
    foreach ($c in $xml.SelectNodes('//Compile[@Include]')) {
        $path = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($dir, $c.Include))
        if (-not (Test-Path -LiteralPath $path)) { throw "$full compiles $($c.Include), which does not exist" }
        $files += Get-Item -LiteralPath $path
    }
    foreach ($r in $xml.SelectNodes('//ProjectReference[@Include]')) {
        if ($r.Include -match '\$\(') { continue }
        $files += Get-ProjectSources ([System.IO.Path]::Combine($dir, $r.Include)) $seen
    }
    $files
}
function Newest-Source([string]$csproj) {
    try { $files = @(Get-ProjectSources $csproj @{}) } catch { L "cannot enumerate sources of ${csproj}: $_ — building"; return [DateTime]::MaxValue }
    if ($files.Count -eq 0) { return [DateTime]::MaxValue }  # can't see sources → force a build
    ($files | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
}
function Newest-Output([string]$dll) {
    (Get-ChildItem -LiteralPath (Split-Path $dll -Parent) -File -ErrorAction SilentlyContinue |
        Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
}

$failed = @()
$mutex = New-Object System.Threading.Mutex($false, 'ClaudeRoslynLspBuild')
$held = $false
try { $held = $mutex.WaitOne([TimeSpan]::FromMinutes(5)) } catch [System.Threading.AbandonedMutexException] { $held = $true } catch { $held = $true }
try {
    foreach ($p in $projects) {
        $csproj  = Join-Path $Root $p.csproj
        $dll     = Join-Path $Root $p.dll
        if (-not (Test-Path $csproj)) { L "SKIP $($p.name): csproj not found ($csproj)"; continue }

        $needs = $true
        if (Test-Path $dll) {
            $needs = (Newest-Output $dll) -lt (Newest-Source $csproj)
        }
        if (-not $needs) { L "$($p.name) up-to-date"; continue }

        L "building $($p.name) ..."
        $out = & dotnet build $csproj -c Release -v quiet --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            L "BUILD FAILED $($p.name) (exit $LASTEXITCODE): $($out -join "`n")"
            $failed += $p.name
        }
        else { L "built $($p.name)" }
    }
}
finally { if ($held) { try { $mutex.ReleaseMutex() } catch {} } }

# A failed build here means the plugin keeps RUNNING ITS OLD BINARIES — the most misleading failure mode there is, since
# everything still works, just at whatever version last built. It went unnoticed for weeks as a log line. Say it out loud
# (SessionStart output reaches the agent) so a stale deploy can never again be the quiet explanation for a fixed bug.
if ($failed.Count -gt 0) {
    Write-Host "[roslyn-lsp] BUILD FAILED for: $($failed -join ', ') — the LSP/MCP is running STALE binaries." -ForegroundColor Red
    Write-Host "[roslyn-lsp] see $logFile — until this builds, fixes to the plugin are NOT deployed."
}

# --- Pre-warm the shared daemon for THIS workspace ---------------------------------------------------------------
# The first .cs edit otherwise pays a cold start (daemon boot + loading a 300-project solution) while Claude's LSP
# host is mid-handshake — long enough that the client connects then gets torn down. Starting the daemon here, at
# SessionStart, means the first edit connects to an already-warm owner. Idempotent: the daemon's own singleton mutex
# makes any duplicate exit instantly, and we skip when one is already alive.
function PreWarm-Daemon {
    $ws = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($ws)) {
        # Run by hand rather than by the hook: the cwd is whatever the shell happened to be in. Only prewarm if it is
        # actually a workspace — otherwise we start a daemon that indexes e.g. the user's home directory and serves no one.
        $ws = (Get-Location).Path
        $isWorkspace = (Test-Path (Join-Path $ws '.git')) -or
                       (Get-ChildItem -Path $ws -Filter *.sln -File -ErrorAction SilentlyContinue) -or
                       (Get-ChildItem -Path $ws -Filter *.slnx -File -ErrorAction SilentlyContinue)
        if (-not $isWorkspace) { L "prewarm SKIP: no CLAUDE_PROJECT_DIR and cwd is not a workspace ($ws)"; return }
    }
    $daemonDll = Join-Path $Root 'daemon\bin\Release\net8.0\ClaudeRoslynLsp.Daemon.dll'
    if (-not (Test-Path $daemonDll)) { L "prewarm SKIP: daemon dll missing ($daemonDll)"; return }

    # Endpoint hash must match PipeKey.ForRoot exactly: normalize (\\→/, trim trailing /, lowercase), SHA256, first 16 hex.
    $norm = ($ws -replace '\\', '/').TrimEnd('/').ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($norm))
    $endpoint = 'roslyn-lsp-' + ([System.Convert]::ToHexString($sha)).ToLowerInvariant().Substring(0, 16)

    # Liveness: the daemon holds "<endpoint>-daemon" for its whole life. If we can take it, none is running.
    $alive = $false
    $m = New-Object System.Threading.Mutex($false, "$endpoint-daemon")
    try { if ($m.WaitOne(0)) { try { $m.ReleaseMutex() } catch {} } else { $alive = $true } }
    catch [System.Threading.AbandonedMutexException] { } catch { }
    finally { $m.Dispose() }
    if ($alive) { L "prewarm: daemon already alive for $ws ($endpoint)"; return }

    L "prewarm: starting daemon for $ws ($endpoint)"
    # Launch through run.ps1 so the daemon runs from a shadow copy and never locks daemon/bin (rebuild-safe at any N).
    $runPs1 = Join-Path $Root 'boot\run.ps1'
    try { Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runPs1, 'daemon', '--detached', '--root', $ws) -WindowStyle Hidden | Out-Null }
    catch { L "prewarm: failed to start daemon: $($_.Exception.Message)" }
}
PreWarm-Daemon
