<#
  lsp-patch.ps1 — the plugin's setup dependency on Claude Code's builtin LSP tool.

  Claude Code's `LSP` tool (diagnostics / go-to-def / find-refs) is gated behind a client patch: tweakcc's
  `fix-lsp-support` ("Enable/fix nascent LSP support"). Without it, a registered `.cs` LSP server is never spawned —
  ENABLE_LSP_TOOL=1 alone is not enough on current builds. And **Claude Code auto-updates silently revert the patch**
  (the binary is replaced), so it must be re-applied after every update.

  NOTE (2026-07-23): recent Claude Code builds (verified on 2.1.218) spawn the registered `.lsp.json` C# server
  NATIVELY — the tweakcc patch is no longer required on those versions. `check` is therefore FUNCTIONAL, not a bare
  marker test: it passes when CC is observed spawning the LSP client, and only falls back to warning about the legacy
  patch when the tool is genuinely unconfirmed. This stops the check crying wolf every SessionStart on a version that
  needs no patch (which surfaced as a spurious "SessionStart hook error / non-blocking status / no stderr").

  This script owns that dependency:
    -Mode check  → is the builtin LSP tool working for the CURRENTLY-INSTALLED CC version? Passes if the tweakcc patch
                   is applied (version-keyed marker) OR CC spawns the registered server natively (a running LSP client
                   → writes a native-ok marker so later sessions pass instantly). Warns + exits 3 only when neither.
    -Mode apply  → install tweakcc (global if present, else npx) and apply `fix-lsp-support`, then write the marker.
                   The CC binary can only be patched while NO Claude process is running (Windows locks the running .exe).
                   In a multi-agent setup that means an all-sessions-closed window; this script refuses (cleanly) if any
                   Claude is running rather than failing with EBUSY.

  Exit codes (check): 0 = applied/ok, 3 = missing (needs apply). (apply): 0 = applied/already, 2 = blocked (CC running), 1 = failed.
#>
param([ValidateSet('check', 'apply')][string]$Mode = 'check')

$ErrorActionPreference = 'Continue'
$markerDir = Join-Path $env:LOCALAPPDATA 'claude-roslyn-lsp\lsp-patch'
New-Item -ItemType Directory -Force -Path $markerDir | Out-Null
$logFile = Join-Path $markerDir 'lsp-patch.log'
function L([string]$m) { "$([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss')) [$Mode] $m" | Out-File -FilePath $logFile -Append -Encoding utf8 }

function Get-CCVersion {
    try { $v = (& claude --version 2>$null); if ($v -match '(\d+\.\d+\.\d+)') { return $Matches[1] } } catch {}
    return 'unknown'
}
function Test-CCRunning {
    # The native install is a single claude.exe; ANY running instance (sibling agents included) locks it.
    @(Get-Process -Name 'claude' -ErrorAction SilentlyContinue).Count
}
function Test-LspClientRunning {
    # CC spawns the registered .lsp.json server as a `dotnet exec …ClaudeRoslynLsp.Client.dll` child when it honors the
    # LSP registration. A running client ⇒ CC started the LSP (natively, or via the patch) ⇒ the tool is functional and
    # the tweakcc patch is not needed. Cross-session: any sibling agent's running client proves native spawn for this CC.
    try {
        @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match 'ClaudeRoslynLsp\.Client' }).Count
    } catch { 0 }
}

$ccVersion = Get-CCVersion
$marker = Join-Path $markerDir "applied-$ccVersion.marker"
$nativeMarker = Join-Path $markerDir "native-ok-$ccVersion.marker"
$patchApplied = Test-Path $marker
$nativeOk = Test-Path $nativeMarker

if ($Mode -eq 'check') {
    if ($patchApplied) { L "patch present for CC $ccVersion"; exit 0 }
    if ($nativeOk) { L "native LSP spawn previously confirmed for CC $ccVersion — patch not needed"; exit 0 }

    # Functional probe: recent CC builds spawn the registered .lsp.json server NATIVELY (no tweakcc patch needed). If the
    # CC-spawned LSP client is running, the tool works regardless of the patch — record it (self-healing) and stop warning.
    if ($ccVersion -ne 'unknown' -and (Test-LspClientRunning) -gt 0) {
        "native LSP confirmed for CC $ccVersion at $([DateTime]::Now.ToString('o')) (client process running)" |
            Out-File -FilePath $nativeMarker -Encoding utf8
        L "native LSP spawn confirmed for CC $ccVersion (client running) — wrote native-ok marker"
        exit 0
    }

    $msg = @"
[claude-roslyn-lsp] The builtin LSP tool is not confirmed working for CC $ccVersion (no patch marker, and no running LSP client observed yet).
Recent CC builds spawn the registered C# server NATIVELY — if the LSP tool returns symbols on a warm index, no patch is needed and this check self-clears once the LSP client process is seen (e.g. after the first LSP call this session).
Only if the LSP tool genuinely returns nothing on a warm index, apply the legacy tweakcc patch (requires ALL Claude sessions closed):
    pwsh -NoProfile -File "$PSCommandPath" -Mode apply
Then relaunch Claude. (The roslyn refactoring MCP works regardless.)
"@
    Write-Output $msg
    L "unconfirmed for CC $ccVersion — warned (no marker, no client seen)"
    exit 3
}

# --- apply ---
if ($patchApplied) { L "already applied for CC $ccVersion"; Write-Output "[claude-roslyn-lsp] LSP patch already applied for CC $ccVersion."; exit 0 }

$running = Test-CCRunning
if ($running -gt 0) {
    $m = "[claude-roslyn-lsp] Cannot patch: $running Claude process(es) running — Windows locks the running claude.exe. Close ALL Claude sessions (coordinate the multi-agent restart) and re-run with -Mode apply."
    Write-Output $m; L "blocked — $running CC running"; exit 2
}

# Prefer a globally-installed tweakcc (fast); fall back to npx.
$tweak = Get-Command tweakcc -ErrorAction SilentlyContinue
if ($tweak) { $cmd = @('tweakcc') } else { $cmd = @('npx', '-y', 'tweakcc@latest') }

# FULL apply — NOT `--patches fix-lsp-support`. fix-lsp-support is an "Always Applied" patch, not a *configurable*
# one; `--patches <id>` filters to configurable patches, so scoping it to fix-lsp-support matched nothing and patched
# NOTHING while still exiting 0 (verified: the unpacked JS was byte-identical to the stock binary). `tweakcc --apply`
# with no filter applies the always-applied set (including fix-lsp-support) plus the user's configured patches — this
# is the form the claude-code-lsps README prescribes and the one known to actually enable the LSP tool.
$backup = Join-Path $env:USERPROFILE '.tweakcc\native-binary.backup'
L "applying (full tweakcc --apply) via: $($cmd -join ' ')"
$out = & $cmd[0] @($cmd[1..($cmd.Count-1)] + @('--apply')) 2>&1
if ($LASTEXITCODE -ne 0 -or ($out -match 'EBUSY')) {
    Write-Output "[claude-roslyn-lsp] tweakcc apply FAILED:`n$($out -join "`n")"
    L "apply FAILED: $($out -join ' | ')"; exit 1
}

# VERIFY the patch actually landed in the code — a 0 exit is NOT proof (the scoped form exited 0 yet changed nothing).
# Unpack the freshly-patched binary and the stock backup; if their embedded JS is identical, the patch was a no-op.
$claudeExe = (Get-Command claude -ErrorAction SilentlyContinue)?.Source
if ($claudeExe -and (Test-Path $backup)) {
    $tmp = Join-Path $env:TEMP 'crlsp-verify'
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $curJs = Join-Path $tmp 'cur.js'; $bakJs = Join-Path $tmp 'bak.js'
    & $cmd[0] @($cmd[1..($cmd.Count-1)] + @('unpack', $curJs, $claudeExe)) 2>&1 | Out-Null
    & $cmd[0] @($cmd[1..($cmd.Count-1)] + @('unpack', $bakJs, $backup)) 2>&1 | Out-Null
    if ((Test-Path $curJs) -and (Test-Path $bakJs)) {
        $curHash = (Get-FileHash $curJs -Algorithm SHA256).Hash
        $bakHash = (Get-FileHash $bakJs -Algorithm SHA256).Hash
        Remove-Item $curJs, $bakJs -ErrorAction SilentlyContinue
        if ($curHash -eq $bakHash) {
            Write-Output "[claude-roslyn-lsp] tweakcc reported success but the patched JS is IDENTICAL to stock — NO patch landed. LSP not enabled. (tweakcc may not support CC $ccVersion.)"
            L "VERIFY FAILED — patched JS == stock (no-op apply) for CC $ccVersion"; exit 1
        }
        L "verify OK — patched JS differs from stock"
    }
}

"patched CC $ccVersion at $([DateTime]::Now.ToString('o'))" | Out-File -FilePath $marker -Encoding utf8
Write-Output "[claude-roslyn-lsp] Applied the LSP patch for CC $ccVersion (verified JS changed). Relaunch Claude; the C# LSP starts on first use."
L "applied + verified for CC $ccVersion"
exit 0
