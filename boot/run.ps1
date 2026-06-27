<#
  run.ps1 — shadow-copy launcher. The ONE entry point for every component (daemon, client, mcp, cli).

  Why this exists: `dotnet exec bin/X.dll` memory-maps and LOCKS X.dll (+ every dependency, including the shared
  Sluice.dll) for the whole life of the process. With N Claude instances each holding a daemon/client/mcp, the build
  outputs stay persistently locked and `ensure-built`'s rebuild fails with MSB3027 "being used by another process".

  The fix: never run from `bin`. `bin` is a pure BUILD output — only ever written, never loaded. This script copies the
  freshly-built output to a per-build shadow directory under %LOCALAPPDATA% and execs from THERE, so:
    - the build output (`bin`) is never locked -> rebuilds always succeed, at any number of concurrent instances;
    - each distinct build (identified by the newest mtime in `bin`) gets its own shadow dir, so a rebuild mid-session
      produces a NEW shadow the next launch picks up, while running processes keep their own (older) shadow alive;
    - stale shadows (superseded builds / exited processes) are reaped best-effort on each launch.

  stdio: the child inherits our console/pipe handles directly (UseShellExecute=$false, no redirection), so the LSP
  Content-Length wire and the MCP NDJSON wire pass through as clean bytes — PowerShell never sees or re-encodes them.

  Args are parsed manually from $args (NOT a param block) so passthrough flags like --root / --gpu can't collide with
  PowerShell parameter binding. Invoke with `pwsh -NoProfile -File run.ps1 <project> [--detached] [passthrough...]`:
    pwsh -NoProfile -File run.ps1 client                       # foreground, inherit stdio, wait, propagate exit
    pwsh -NoProfile -File run.ps1 mcp
    pwsh -NoProfile -File run.ps1 daemon --detached --root <ws> # spawn detached, return immediately
    pwsh -NoProfile -File run.ps1 cli symbols File.cs
#>
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent

# --- manual arg parse: first known project name -> $Project; '--detached' -> flag; everything else -> passthrough ----
$Project = $null
$Detached = $false
$Rest = [System.Collections.Generic.List[string]]::new()
foreach ($a in $args) {
    if (-not $Project -and @('daemon', 'client', 'mcp', 'cli') -contains $a) { $Project = $a; continue }
    if ($a -eq '--detached') { $Detached = $true; continue }
    $Rest.Add([string]$a)
}
function Log([string]$m) { [Console]::Error.WriteLine("[run:$Project] $m") }
if (-not $Project) { Log "usage: run.ps1 <daemon|client|mcp|cli> [--detached] [args...]"; exit 2 }

# project -> (bin subdir, primary dll). TFMs pinned to each csproj's <TargetFramework>.
$map = @{
    daemon = @{ bin = 'daemon\bin\Release\net8.0'; dll = 'ClaudeRoslynLsp.Daemon.dll' }
    client = @{ bin = 'client\bin\Release\net8.0'; dll = 'ClaudeRoslynLsp.Client.dll' }
    mcp    = @{ bin = 'mcp\bin\Release\net10.0';   dll = 'ClaudeRoslynLsp.Mcp.dll' }
    cli    = @{ bin = 'cli\bin\Release\net8.0';     dll = 'crlsp.dll' }
}
$p = $map[$Project]
$binDir = Join-Path $Root $p.bin
$binDll = Join-Path $binDir $p.dll
if (-not (Test-Path $binDll)) { Log "build output missing: $binDll (run ensure-built.ps1 first)"; exit 1 }

# Stamp the whole build by the NEWEST file mtime in bin — catches a dependency-only rebuild (e.g. Sluice.dll) even when
# the primary dll didn't change. Cheap over the few-dozen files in an output dir.
$newest = (Get-ChildItem $binDir -File -ErrorAction SilentlyContinue | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
$stamp = ('{0:x}' -f $newest.Ticks)
$shadowBase = Join-Path $env:LOCALAPPDATA "claude-roslyn-lsp\shadow\$Project"
$shadowDir = Join-Path $shadowBase $stamp
$shadowDll = Join-Path $shadowDir $p.dll

# Copy-if-missing. Copy to a unique temp dir then atomically rename into place, so two instances racing the first launch
# after a build can't see a half-populated shadow (the loser's temp is discarded).
if (-not (Test-Path $shadowDll)) {
    New-Item -ItemType Directory -Force -Path $shadowBase | Out-Null
    $tmp = "$shadowDir.tmp-$PID"
    try {
        $null = robocopy $binDir $tmp /E /NJH /NJS /NP /NDL /NFL /R:1 /W:1
        if (-not (Test-Path $shadowDir)) { Move-Item -LiteralPath $tmp -Destination $shadowDir -ErrorAction Stop }
    } catch { } finally { if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } }
    Get-ChildItem $shadowBase -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne $stamp } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}
if (-not (Test-Path $shadowDll)) { Log "shadow copy failed; falling back to bin (will lock $($p.dll))"; $shadowDll = $binDll }

# dotnet exec <shadowDll> <passthrough...>
$execArgs = [System.Collections.Generic.List[string]]::new()
$execArgs.Add('exec'); $execArgs.Add($shadowDll)
foreach ($a in $Rest) { $execArgs.Add($a) }

if ($Detached) {
    # Daemon: fire-and-forget. It talks to Roslyn over its own redirected stdio and to clients over shared memory, so it
    # needs no inherited console. Start hidden + detached and return immediately (no babysitter left behind).
    Start-Process -FilePath 'dotnet' -ArgumentList $execArgs -WindowStyle Hidden | Out-Null
    exit 0
}

# Client / MCP / CLI: inherit our stdio so the LSP / MCP wire stays byte-clean, wait, propagate the exit code.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
foreach ($a in $execArgs) { $psi.ArgumentList.Add($a) }
$psi.UseShellExecute = $false   # inherit console/pipe handles; no redirection -> child owns stdin/stdout/stderr directly
$proc = [System.Diagnostics.Process]::Start($psi)
$proc.WaitForExit()
exit $proc.ExitCode
