<#
  lsp-patch.ps1 — the plugin's setup dependency on Claude Code's builtin LSP tool.

  Claude Code's `LSP` tool (diagnostics / go-to-def / find-refs) is gated behind a client patch: tweakcc's
  `fix-lsp-support` ("Enable/fix nascent LSP support"). Without it, a registered `.cs` LSP server is never spawned —
  ENABLE_LSP_TOOL=1 alone is not enough on current builds. And **Claude Code auto-updates silently revert the patch**
  (the binary is replaced), so it must be re-applied after every update.

  This script owns that dependency:
    -Mode check  → is the patch applied for the CURRENTLY-INSTALLED CC version? (version-keyed marker) Prints guidance
                   if not. Used by the SessionStart hook so every session re-checks — and re-warns after an update.
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

$ccVersion = Get-CCVersion
$marker = Join-Path $markerDir "applied-$ccVersion.marker"
$patchApplied = Test-Path $marker

if ($Mode -eq 'check') {
    if ($patchApplied) { L "patch present for CC $ccVersion"; exit 0 }
    $msg = @"
[claude-roslyn-lsp] Claude Code's builtin LSP tool is NOT patched for CC $ccVersion.
The C# language server cannot start until it is. CC auto-updates revert this patch, so it must be re-applied.
To apply (requires ALL Claude sessions closed):
    pwsh -NoProfile -File "$PSCommandPath" -Mode apply
Then relaunch Claude. (The roslyn refactoring MCP works regardless; only the LSP tool needs this.)
"@
    Write-Output $msg
    L "patch MISSING for CC $ccVersion — warned"
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
L "applying fix-lsp-support via: $($cmd -join ' ')"
$out = & $cmd[0] @($cmd[1..($cmd.Count-1)] + @('--apply', '--patches', 'fix-lsp-support')) 2>&1
$ok = $LASTEXITCODE -eq 0 -and ($out -notmatch 'EBUSY')
if ($ok) {
    "patched CC $ccVersion at $([DateTime]::Now.ToString('o'))" | Out-File -FilePath $marker -Encoding utf8
    Write-Output "[claude-roslyn-lsp] Applied fix-lsp-support for CC $ccVersion. Relaunch Claude; the C# LSP will start on first use."
    L "applied OK for CC $ccVersion"
    exit 0
}
else {
    Write-Output "[claude-roslyn-lsp] tweakcc apply FAILED:`n$($out -join "`n")"
    L "apply FAILED: $($out -join ' | ')"
    exit 1
}
