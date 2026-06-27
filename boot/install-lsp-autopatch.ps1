<#
  install-lsp-autopatch.ps1 — opt-in, hands-free re-application of the LSP patch across Claude Code auto-updates.

  Run ONCE. Registers a per-user Windows Scheduled Task that runs `lsp-patch.ps1 -Mode apply` at logon and hourly.
  That apply is a safe no-op when Claude is running or already patched for the current version; it only does real work
  in an all-sessions-closed window after an update has reverted the patch. In a multi-agent setup the window may be
  infrequent (overnight, a coordinated restart) — the task simply waits for one. Pairs with the SessionStart hook, which
  warns immediately when the patch is missing.

  Remove with:  Unregister-ScheduledTask -TaskName 'ClaudeRoslynLsp-AutoPatch' -Confirm:$false
#>
param(
    [string]$PatchScript = (Join-Path $PSScriptRoot 'lsp-patch.ps1'),
    [int]$IntervalHours = 1
)

$ErrorActionPreference = 'Stop'
$PatchScript = (Resolve-Path $PatchScript).Path
$taskName = 'ClaudeRoslynLsp-AutoPatch'

$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue)?.Source
if (-not $pwsh) { $pwsh = (Get-Command powershell).Source }

$action = New-ScheduledTaskAction -Execute $pwsh `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PatchScript`" -Mode apply"

$atLogon = New-ScheduledTaskTrigger -AtLogOn
$repeat = New-ScheduledTaskTrigger -Once -At ([DateTime]::Today.AddMinutes(5)) `
    -RepetitionInterval (New-TimeSpan -Hours $IntervalHours)

$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew

# Preferred: a scheduled task (logon + hourly) — catches any all-closed window, including mid-day. Needs rights to
# register a task; some locked-down machines deny this. Fall back to a per-user logon Run key (no elevation), which
# applies the patch at each logon — when Claude is not yet running, so the binary is unlocked.
$installed = $false
try {
    try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop } catch {}
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($atLogon, $repeat) `
        -Settings $settings -Description 'Re-applies tweakcc fix-lsp-support to Claude Code after updates (no-op unless needed). Owned by the claude-roslyn-lsp plugin.' -ErrorAction Stop | Out-Null
    Write-Output "Registered scheduled task '$taskName' -> applies the patch at logon + every ${IntervalHours}h, in any all-Claude-closed window."
    Write-Output "Remove with: Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false"
    $installed = $true
}
catch {
    Write-Output "Scheduled task registration unavailable ($($_.Exception.Message.Trim())). Falling back to a per-user logon entry (no elevation)."
}

if (-not $installed) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $cmd = "`"$pwsh`" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PatchScript`" -Mode apply"
    Set-ItemProperty -Path $runKey -Name 'ClaudeRoslynLspAutoPatch' -Value $cmd
    Write-Output "Installed per-user logon Run entry 'ClaudeRoslynLspAutoPatch' -> applies the patch at each logon (Claude not yet running = binary unlocked)."
    Write-Output "Note: logon-only — a mid-day CC update is re-applied at the next logon/reboot. The SessionStart hook warns in the meantime."
    Write-Output "Remove with: Remove-ItemProperty -Path '$runKey' -Name 'ClaudeRoslynLspAutoPatch'"
}
