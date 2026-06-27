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

try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop } catch {}

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($atLogon, $repeat) `
    -Settings $settings -Description 'Re-applies tweakcc fix-lsp-support to Claude Code after updates (no-op unless needed). Owned by the claude-roslyn-lsp plugin.' | Out-Null

Write-Output "Registered scheduled task '$taskName' → $pwsh -File `"$PatchScript`" -Mode apply (at logon + every ${IntervalHours}h)."
Write-Output "It applies the patch in the next all-Claude-closed window and after each CC update. Remove with: Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false"
