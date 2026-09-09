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

    # 🩸 SAY ONLY WHAT THE MUTEX PROVES. This used to render as "already indexed and SERVING", and the probe cannot
    # support that word: the daemon holds "<endpoint>-daemon" FOR ITS WHOLE LIFE (the comment above says so), so a
    # held mutex distinguishes "a process exists" from "no process exists" and nothing else. Not indexed. Not
    # answering. Not answering YOU.
    #
    # ⚖️ MEASURED 2026-09-09 BY FOUR AGENTS, and it is the exact gap: two sessions had every LSP call time out at
    # 120 s while two others were served normally BY THE SAME DAEMON — the fault was per-session CLIENT state (the
    # known "client N read loop ended (NullReferenceException)", which strands one client while the server serves
    # everyone else). Both stranded sessions had been told "already indexed and serving" at start, and one spent
    # the evening concluding the daemon was wedged and should be reaped. It was serving 16% of a core at the time.
    # A banner that overstates its probe does not merely mislead; it points the reader at the wrong subject.
    $state = if ($warm) {
        'A DAEMON IS RUNNING for this workspace (a process holds the endpoint mutex — that is liveness, NOT proof it is indexed, and NOT proof it will answer this session).'
    } else {
        'no daemon held the endpoint yet, so one is starting for this workspace; it indexes in a few seconds.'
    }

    # 🩸 "ONE SHARED DAEMON SERVES ALL AGENTS" WAS FALSE BY CONSTRUCTION, AND IT CAUSED A FLEET-WIDE MISDIAGNOSIS.
    # @ziltch2 measured 2026-09-09: SIX Microsoft.CodeAnalysis.LanguageServer.exe, six DISTINCT parent MCP hosts.
    # @mesh reproduced it — 7.65 GB of COMMIT across the six (ziltch2's 5.61 GB was working set; the meters differ
    # by ~2x, which is its own trap). The servers are spawned `--stdio`, and A STDIO SERVER IS ONE PROCESS PER
    # CLIENT CONNECTION: "shared daemon" and `--stdio` are mutually exclusive, so no wording could have made the old
    # sentence true. The endpoint mutex this banner reads proves A process holds it, never that there is only one.
    #
    # ⚖️ AND IT RE-EXPLAINS THE TIMEOUTS. Two of the six had held 5 MB and 67 MB resident for FOURTEEN HOURS — they
    # never loaded a workspace at all. A session attached to one of those does not have a slow server, it has an
    # EMPTY one, and it times out on every call forever while a sibling on the warm 4.6 GB server is served
    # instantly. The earlier reading (including mine) was "same daemon, so the fault is client-side" — same symptom,
    # opposite cause, and "just retry" cannot fix the cold-server case because the retry reconnects to the same host.
    $servers = @(Get-CimInstance Win32_Process -Filter "Name='Microsoft.CodeAnalysis.LanguageServer.exe'" -ErrorAction SilentlyContinue)
    $serverLine = if ($servers.Count -gt 0) {
        $gb = 0.0
        foreach ($s in $servers) { $p = Get-Process -Id $s.ProcessId -ErrorAction SilentlyContinue; if ($p) { $gb += $p.PrivateMemorySize64 / 1GB } }
        "{0} language server(s) alive on this box, {1:N1} GB committed between them — ONE PER MCP HOST (they are --stdio), not one shared." -f $servers.Count, $gb
    } else { 'no language server process is running yet; yours starts with your first call.' }

    $banner = @"
[Roslyn LSP] C# language server, solution-wide and self-warming. $serverLine $state

PREFER the LSP over grep for any symbol / reference / type question — it is semantic (no string false-positives) and solution-wide:
- workspaceSymbol  — find a type or member by name across the WHOLE solution (the thing to reach for instead of grepping for a definition)
- findReferences   — exact callers / blast-radius before editing a shared type (grep under- and over-counts)
- goToDefinition / goToImplementation / typeDefinition  — navigate, incl. across the parallel-interface sprawl
- hover            — signature + XML-doc summary
- get_diagnostics  — current compiler + analyzer diagnostics for a file WITHOUT a build

FIRST-CALL COLD EDGE: in a brand-new session the very first call can return "server is starting / has not finished indexing" — retry once, it warms in seconds; do not conclude the LSP is unreliable. A "no symbols found" result is a true empty answer only once indexed — if unsure, query a symbol you know exists to confirm the index is live before trusting a negative.

IF YOUR CALLS TIME OUT, IT IS PROBABLY YOUR CLIENT, NOT THE DAEMON — measured 2026-09-09, four agents, one daemon: two sessions had every call time out at 120s while two others were served normally by that same daemon. The known fault is per-session ("client N read loop ended (NullReferenceException)" strands one client; the server keeps serving everyone else), so:
- THERE ARE TWO DIFFERENT CAUSES AND THE CURES ARE OPPOSITE. (a) A WEDGED CLIENT: a timed-out call now drops its cached connection, so the NEXT call reconnects — just retry. Before 2026-09-09 it did not (the host's timeout fired before the client's own ceiling, the drop was skipped, one wedged link poisoned the session), and on older bits there is no in-session cure because MCP attaches at SESSION START. (b) A COLD SERVER: your MCP host has its OWN server, and if that one never loaded the workspace it will time out on EVERY call, forever, while other sessions are served instantly by theirs. Retrying reconnects you to the SAME host and therefore the same cold server — it cannot help. Measured 2026-09-09: two of six servers had held 5 MB and 67 MB resident for fourteen hours, i.e. had never loaded anything.
- TO TELL THEM APART: ask whether ANOTHER session is being served (it will be — theirs is a different server, which proves nothing about yours), then look at YOUR host's server. A warm one on this solution is GBs; a cold one is tens of MB. If yours is the cold one, a new session is the only cure and grep is the honest fallback until then.
- It is NOT reaping the daemon. Reaping discards a multi-GB warm index that is serving other sessions.
- DO NOT diagnose the daemon by CPU. It is request/response, so 0% between requests is its CORRECT state — "flat CPU while elapsed climbs = wedged" is a rule for work that should be CONTINUOUS (a replay, a build, a boot) and it inverts here. An idle server and a wedged one look identical; only sending a request tells them apart.
- Before concluding anything about the daemon, ask whether ANOTHER session is being served. If yes, the daemon is fine and the fault is yours to reset.
"@

    Emit $banner
} catch {
    # Never break the session over a banner.
}
