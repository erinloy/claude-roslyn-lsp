#requires -Version 7
# SessionStart presence banner for the Roslyn LSP.
#
# WHY: agents reported (mesh, 2026-06-27) that the warm, solution-wide LSP already works — workspaceSymbol /
# findReferences are instant and self-warming with no per-session load — but they DEFAULT TO GREP because nothing tells
# them it's live. A buried "Use LSP tools" instruction doesn't flip the default; a loud SessionStart presence line does.
# This emits that line as SessionStart additionalContext, plus the one piece of operational guidance that keeps a first
# call from poisoning the habit: the fresh-session cold edge (first call may report "indexing" — retry, it warms in secs).
#
# Output contract: a single JSON object on stdout with hookSpecificOutput.additionalContext (the SessionStart context
# channel). Must never fail the hook chain — any error degrades to no banner, never a broken session.

param([string]$Root = $env:CLAUDE_PLUGIN_ROOT)

function Emit([string]$text) {
    $obj = @{
        hookSpecificOutput = @{
            hookEventName     = 'SessionStart'
            additionalContext = $text
        }
    }
    ($obj | ConvertTo-Json -Depth 5 -Compress)
}

try {
    # Is a daemon already warm for this workspace? (Same liveness signal ensure-built uses: the daemon holds
    # "<endpoint>-daemon" for its whole life. Taking the mutex means none is running yet — it's warming.)
    $ws = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($ws)) { $ws = (Get-Location).Path }
    $norm = ($ws -replace '\\', '/').TrimEnd('/').ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($norm))
    $endpoint = 'roslyn-lsp-' + ([System.Convert]::ToHexString($sha)).ToLowerInvariant().Substring(0, 16)

    $warm = $false
    $m = New-Object System.Threading.Mutex($false, "$endpoint-daemon")
    try { if ($m.WaitOne(0)) { try { $m.ReleaseMutex() } catch {} } else { $warm = $true } }
    catch [System.Threading.AbandonedMutexException] { } catch { } finally { $m.Dispose() }

    $state = if ($warm) {
        'WARM NOW (a shared daemon for this workspace is already indexed and serving).'
    } else {
        'warming (a daemon was just started for this workspace; it indexes in a few seconds).'
    }

    $banner = @"
[Roslyn LSP READY] C# language server is solution-wide and self-warming — one shared daemon serves all agents, no per-session workspace load. $state

PREFER the LSP over grep for any symbol / reference / type question — it is semantic (no string false-positives) and solution-wide:
- workspaceSymbol  — find a type or member by name across the WHOLE solution (the thing to reach for instead of grepping for a definition)
- findReferences   — exact callers / blast-radius before editing a shared type (grep under- and over-counts)
- goToDefinition / goToImplementation / typeDefinition  — navigate, incl. across the parallel-interface sprawl
- hover            — signature + XML-doc summary
- get_diagnostics  — current compiler + analyzer diagnostics for a file WITHOUT a build

FIRST-CALL COLD EDGE: in a brand-new session the very first call can return "server is starting / has not finished indexing" — retry once, it warms in seconds; do not conclude the LSP is unreliable. A "no symbols found" result is a true empty answer only once indexed — if unsure, query a symbol you know exists to confirm the index is live before trusting a negative.
"@

    Emit $banner
} catch {
    # Never break the session over a banner.
}
