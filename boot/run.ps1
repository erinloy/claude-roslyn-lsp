<#
  run.ps1 — shadow-copy launcher. The ONE entry point for every component (daemon, client, mcp, cli).

  Why this exists: `dotnet exec bin/X.dll` memory-maps and LOCKS X.dll (+ every dependency, including the shared
  Sluice.dll) for the whole life of the process. With N Claude instances each holding a daemon/client/mcp, the build
  outputs stay persistently locked and `ensure-built`'s rebuild fails with MSB3027 "being used by another process".

  The fix: never run from `bin`. `bin` is a pure BUILD output — only ever written, never loaded. This script copies the
  freshly-built output to a per-build shadow directory under the plugin data root (boot/data-root.ps1:
  CLAUDE_PLUGIN_DATA\roslyn, else ZILTCH_DATA_ROOT\claude-roslyn-lsp, else Z:\DATA\claude-roslyn-lsp, else it fails —
  never AppData, Erin 2026-09-17) and execs from THERE, so:
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

# THE ONE DATA-ROOT RULE (boot/data-root.ps1, twin of shared/PluginDataRoot.cs). No root is a hard failure with the
# resolver's own message, never a guessed directory: the shadow used to live under %LOCALAPPDATA%, which was wiped
# wholesale on 2026-09-17 — the day this moved.
. (Join-Path $PSScriptRoot 'data-root.ps1')
try { $dataRoot = Resolve-PluginDataRoot } catch { Log $_.Exception.Message; exit 1 }
$shadowBase = [System.IO.Path]::Combine($dataRoot, 'shadow', $Project)
$shadowDir = Join-Path $shadowBase $stamp
$shadowDll = Join-Path $shadowDir $p.dll

# Copy-if-missing. Copy to a unique temp dir then atomically rename into place, so two instances racing the first launch
# after a build can't see a half-populated shadow (the loser's temp is discarded).
#
# 🩸 THE GUARD IS A COMPLETION MARKER, NOT THE PAYLOAD DLL, AND THAT DISTINCTION COST A DEAD MCP SERVER.
# MEASURED 2026-09-01: shadow\mcp\<stamp>\ held 33 files, ALL .dll, NO .json — including no
# ClaudeRoslynLsp.Mcp.runtimeconfig.json. A framework-dependent app cannot start without it: the host falls back to
# "self-contained", looks for hostpolicy.dll, does not find it, and dies with a message about hostpolicy that names
# neither the real missing file nor the shadow copy as the cause. Claude Code reported only "Connection closed".
# ⇒ The old guard was `Test-Path $shadowDll`. `ClaudeRoslynLsp.Mcp.dll` WAS present, so the guard read the shadow as
#   complete and skipped the copy FOREVER. One incomplete promotion became permanent, and no retry could heal it:
#   inferring a directory's completeness from ONE file inside it is the same absence-reads-as-normal defect that
#   two-state instruments have, applied to a filesystem.
# ⇒ The marker is written INTO $tmp BEFORE the rename, so it appears atomically WITH the content it certifies. A
#   marker written after the move would itself be a window where the shadow looks complete and is not.
$marker = Join-Path $shadowDir '.shadow-complete'
if (-not (Test-Path $marker)) {
    New-Item -ItemType Directory -Force -Path $shadowBase | Out-Null
    # A shadow that failed verification must not be left standing: it is what the old guard would trust next time.
    if (Test-Path $shadowDir) { Remove-Item $shadowDir -Recurse -Force -ErrorAction SilentlyContinue }
    $tmp = "$shadowDir.tmp-$PID"
    try {
        $null = robocopy $binDir $tmp /E /NJH /NJS /NP /NDL /NFL /R:1 /W:1
        # 🛑 ROBOCOPY'S EXIT CODE WAS DISCARDED, WHICH IS HOW AN INCOMPLETE COPY GOT PROMOTED IN THE FIRST PLACE.
        # Robocopy is not a normal exit-code citizen: 0-7 are success (0 = nothing to do, 1 = files copied, 2 = extras,
        # 4 = mismatches), and >= 8 means at least one file FAILED to copy. Promoting on failure publishes a partial
        # directory under the name everything else trusts.
        $rc = $LASTEXITCODE
        if ($rc -ge 8) { throw "robocopy failed with exit $rc copying $binDir" }
        Set-Content -LiteralPath (Join-Path $tmp '.shadow-complete') -Value $stamp -Encoding ascii
        if (-not (Test-Path $shadowDir)) { Move-Item -LiteralPath $tmp -Destination $shadowDir -ErrorAction Stop }
    } catch { Log "shadow copy failed: $_" } finally { if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } }
    # 🩸 THE REAPER DELETED LIVE SHADOWS, AND THAT IS THE 2026-09-01 SHAPE ABOVE. Every launch whose stamp differs — a
    # rebuild, or the repo checkout and the installed copy, which resolve the same data root and so share this base —
    # removed every other stamp, including the one a running host executes from. Remove-Item -Recurse deletes every
    # file not held open and silently skips the rest. Measured 2026-09-17 on a scratch copy with a live MCP host in it:
    # 42 files → 33, ALL .dll, ZERO .json — exactly "33 files, ALL .dll, NO .json". The marker guard heals the NEXT
    # launch of that stamp; it cannot protect the process already running from it.
    # ⇒ Claim a stale shadow by deleting its ENTRY DLL first. A running host maps its entry assembly and Windows refuses
    #   to delete a mapped image, so success proves nothing runs from the directory — and with the entry gone nothing
    #   new can start from it. Only then is the rest removed. Renaming the directory is NOT such a test (measured: it
    #   succeeds under a live host). A copy in progress (<stamp>.tmp-<pid>) is left alone while its pid is alive.
    Get-ChildItem $shadowBase -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne $stamp } |
        ForEach-Object {
            if ($_.Name -match '\.tmp-(\d+)$') {
                if (Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue) { return }
            }
            else {
                $entry = Join-Path $_.FullName $p.dll
                if (Test-Path -LiteralPath $entry) {
                    try { Remove-Item -LiteralPath $entry -Force -ErrorAction Stop } catch { return }   # in use: live, keep
                }
            }
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
}
if (-not (Test-Path $shadowDll)) { Log "shadow copy failed; falling back to bin (will lock $($p.dll))"; $shadowDll = $binDll }

# ---- GC FOOTPRINT BOUND (Erin-directed 2026-07-31: "Microsoft.CodeAnalysis.LanguageServer is > 12GB RAM AGAIN") ----
# MEASURED, not assumed: the shipped LanguageServer runtimeconfig.json sets `"System.GC.Server": true`, and this box
# has 32 CORES. Server GC allocates ONE HEAP PER CORE, sizes each heap's budget for THROUGHPUT rather than footprint,
# and does not return memory to the OS. In a SERVER that is correct. This is not a server — it is a long-lived
# INTERACTIVE daemon on a SHARED developer box that is also running a live trading body and the webfrontend, driven
# by five agents editing continuously. Measured: 12.61 GB private, 27,481 handles, ~3h uptime, grown from ~8.6 GB.
#
# These are set on the LAUNCHER so they are inherited by the daemon AND by the LanguageServer child it spawns —
# one place, both processes, instead of patching a downloaded server's runtimeconfig that any update overwrites.
#
# GCConserveMemory (0-9) trades throughput for footprint and CANNOT cause an OOM — it makes the GC collect more
# eagerly and release more, which is exactly the tradeoff an IDE-shaped daemon wants. Deliberately NOT a
# GCHeapHardLimit: a hard cap on a workload whose true working set is unknown turns a memory problem into a
# crash-loop, and the LSP is a shared dependency of every agent.
# GCHeapCount caps the per-core heap proliferation that is the actual multiplier here (32 -> 8).
# NOTE: .NET GC numeric env knobs are parsed as HEX; 8 is unambiguous, values above 9 would not be.
if (-not $env:DOTNET_GCConserveMemory) { $env:DOTNET_GCConserveMemory = '7' }
if (-not $env:DOTNET_GCHeapCount)      { $env:DOTNET_GCHeapCount      = '8' }

# dotnet exec <shadowDll> <passthrough...>
$execArgs = [System.Collections.Generic.List[string]]::new()
$execArgs.Add('exec'); $execArgs.Add($shadowDll)
foreach ($a in $Rest) { $execArgs.Add($a) }

if ($Detached) {
    # Daemon: fire-and-forget. It talks to Roslyn over its own redirected stdio and to clients over shared memory, so it
    # needs no inherited console. Start hidden + detached and return immediately (no babysitter left behind).
    # 🔴 ITS STDOUT USED TO GO NOWHERE. Start-Process with no -RedirectStandardOutput DISCARDS it, so every _log()
    # in the daemon — client connect/disconnect, idle shutdown, document eviction, memory-ceiling recycle — was
    # written into a void. That made the daemon UNOBSERVABLE BY CONSTRUCTION: not merely under-instrumented, but
    # incapable of reporting anything, so no change to it could ever be verified as working. That is the likely
    # reason a long series of fixes here were each believed effective and none demonstrably were — a restart drops
    # memory whether or not the fix runs, and with no log there was nothing else to look at.
    #
    # Append (not truncate) so a recycle's own message survives into the next process's file, and keep it per-project
    # so daemon/client/mcp do not interleave. Cheap: these are low-rate lifecycle lines, not a trace.
    $logRoot = [System.IO.Path]::Combine($dataRoot, 'logs')
    New-Item -ItemType Directory -Force -Path $logRoot -ErrorAction SilentlyContinue | Out-Null
    $outLog = Join-Path $logRoot "$Project.out.log"
    $errLog = Join-Path $logRoot "$Project.err.log"
    try {
        Start-Process -FilePath 'dotnet' -ArgumentList $execArgs -WindowStyle Hidden `
            -RedirectStandardOutput $outLog -RedirectStandardError $errLog | Out-Null
    }
    catch {
        # Redirection can fail if a previous process still holds the file. Losing the log must never stop the daemon
        # from starting — fall back to the original discard-stdout launch.
        Start-Process -FilePath 'dotnet' -ArgumentList $execArgs -WindowStyle Hidden | Out-Null
    }
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
