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

$logDir = Join-Path $env:LOCALAPPDATA 'claude-roslyn-lsp'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir 'ensure-built.log'
function L([string]$m) { "$([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff')) $m" | Out-File -FilePath $logFile -Append -Encoding utf8 }

# project name → (csproj, expected DLL). TFMs are pinned to each csproj's <TargetFramework>.
$projects = @(
    @{ name = 'daemon'; csproj = 'daemon\ClaudeRoslynLsp.Daemon.csproj'; dll = 'daemon\bin\Release\net8.0\ClaudeRoslynLsp.Daemon.dll' },
    @{ name = 'mcp';    csproj = 'mcp\ClaudeRoslynLsp.Mcp.csproj';       dll = 'mcp\bin\Release\net10.0\ClaudeRoslynLsp.Mcp.dll' },
    @{ name = 'client'; csproj = 'client\ClaudeRoslynLsp.Client.csproj'; dll = 'client\bin\Release\net8.0\ClaudeRoslynLsp.Client.dll' }
)

$bridgeDir = Join-Path $Root 'bridge'
function Newest-Source([string]$projDir) {
    $files = @()
    foreach ($dir in @($projDir, $bridgeDir)) {
        if (Test-Path $dir) {
            $files += Get-ChildItem -Path $dir -Recurse -Include *.cs, *.csproj -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        }
    }
    if ($files.Count -eq 0) { return [DateTime]::MaxValue }  # can't see sources → force a build
    ($files | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
}

$mutex = New-Object System.Threading.Mutex($false, 'ClaudeRoslynLspBuild')
$held = $false
try { $held = $mutex.WaitOne([TimeSpan]::FromMinutes(5)) } catch [System.Threading.AbandonedMutexException] { $held = $true } catch { $held = $true }
try {
    foreach ($p in $projects) {
        $csproj  = Join-Path $Root $p.csproj
        $dll     = Join-Path $Root $p.dll
        $projDir = Split-Path $csproj -Parent
        if (-not (Test-Path $csproj)) { L "SKIP $($p.name): csproj not found ($csproj)"; continue }

        $needs = $true
        if (Test-Path $dll) {
            $newest = Newest-Source $projDir
            $needs = (Get-Item $dll).LastWriteTimeUtc -lt $newest
        }
        if (-not $needs) { L "$($p.name) up-to-date"; continue }

        L "building $($p.name) ..."
        $out = & dotnet build $csproj -c Release -v quiet --nologo 2>&1
        if ($LASTEXITCODE -ne 0) { L "BUILD FAILED $($p.name) (exit $LASTEXITCODE): $($out -join "`n")" }
        else { L "built $($p.name)" }
    }
}
finally { if ($held) { try { $mutex.ReleaseMutex() } catch {} } }

# --- Pre-warm the shared daemon for THIS workspace ---------------------------------------------------------------
# The first .cs edit otherwise pays a cold start (daemon boot + loading a 300-project solution) while Claude's LSP
# host is mid-handshake — long enough that the client connects then gets torn down. Starting the daemon here, at
# SessionStart, means the first edit connects to an already-warm owner. Idempotent: the daemon's own singleton mutex
# makes any duplicate exit instantly, and we skip when one is already alive.
function PreWarm-Daemon {
    $ws = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($ws)) { $ws = (Get-Location).Path }
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
    try { Start-Process -FilePath 'dotnet' -ArgumentList @('exec', $daemonDll, '--root', $ws) -WindowStyle Hidden | Out-Null }
    catch { L "prewarm: failed to start daemon: $($_.Exception.Message)" }
}
PreWarm-Daemon
