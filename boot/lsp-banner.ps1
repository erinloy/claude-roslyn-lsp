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
    # ❌ AND THE PART OF THAT READING THAT SAID "TWO OF THEM NEVER LOADED A WORKSPACE" WAS WRONG — WITHDRAWN
    # 2026-09-09 after checking each daemon's own startup log instead of inferring from its size:
    #     124952  C:\SOURCE\scratch\___\echo          "no solution found; opening 64 project(s)"   LOADED
    #      81064  Z:\SOURCE\Ziltch\claude-roslyn-lsp  "no solution found; opening 7 project(s)"    LOADED
    #      77440  ...\Temp\claude\leafcodec-save      "no workspace target — solution/open skipped"
    # 361 MB is 64 real projects and 204 MB is 7. They are small because their workspaces are small, not because
    # nothing happened. Only 77440 opened nothing, and that is CORRECT for a root with no project file: without a
    # project Roslyn can only do single-file analysis, so ~100 MB is the right cost of that job. The cold-server
    # population on this box was ZERO, and the claim existed because three of us read a small number as a broken
    # one. That is why the line the banner now prints quotes the daemon's own words and carries no size threshold.
    # ONE CIM QUERY FOR ALL THREE QUESTIONS. Each Win32_Process query costs ~1.7 s on this box (measured), and this
    # runs on the SessionStart path, so three separate ones put five seconds in front of every agent's first prompt.
    # The combined filter returns 36 rows and every derivation below is then in-memory.
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='Microsoft.CodeAnalysis.LanguageServer.exe'" -ErrorAction SilentlyContinue)
    $servers = @($procs | Where-Object { $_.Name -eq 'Microsoft.CodeAnalysis.LanguageServer.exe' })
    $serverLine = if ($servers.Count -gt 0) {
        $gb = 0.0
        foreach ($s in $servers) { $p = Get-Process -Id $s.ProcessId -ErrorAction SilentlyContinue; if ($p) { $gb += $p.PrivateMemorySize64 / 1GB } }
        "{0} language server(s) alive on this box, {1:N1} GB committed between them — ONE PER MCP HOST (they are --stdio), not one shared." -f $servers.Count, $gb
    } else { 'no language server process is running yet; yours starts with your first call.' }

    # 🔑 NAME *YOUR* SERVER, DO NOT SEND THE READER TO GO FIND IT. Everything below this line used to be prose telling
    # the agent to "ask whether another session is being served, then look at YOUR host's server" — a three-step manual
    # diagnosis, at session start, for something this script is standing right next to. A banner that can measure and
    # instead instructs is making the reader do the instrument's job, and the two sessions that misdiagnosed a healthy
    # daemon on 2026-09-09 both had these instructions in front of them.
    #
    # ⚖️ AND SIZE ALONE IS NOT THE VERDICT, which is why the earlier reading of it was wrong twice in one night.
    # A daemon rooted at 4 loose .cs files with no project CORRECTLY holds ~100 MB forever — Roslyn can only do
    # single-file analysis without a project, so that is the right cost of that job, not a malfunction (@ziltch2's own
    # correction, after citing "0.01 GB for 15.75 h" three times as proof of a broken daemon). The discriminator is
    # not "small", it is "small WHILE A SOLUTION WAS OPENED". The daemon logs `solution/open` when it loads one, so
    # the log answers exactly that and no inference is needed.
    #
    # 📏 COST: one CIM query already being made above, a tail read of at most 64 KB, and no tree walk. The daemon log
    # for this endpoint can be a GIGABYTE (measured: 1.06 GB), so it is seeked from the END and never read whole.
    $mine = $null
    try {
        # ⚠️ THE LOG DIRECTORY IS THE DAEMON'S CHOICE, NOT OURS, AND THE TWO CAN DIFFER. ResolveDataDir picks
        # $CLAUDE_PLUGIN_DATA\roslyn when that is set and %LOCALAPPDATA%\claude-roslyn-lsp otherwise — evaluated in
        # the DAEMON's environment at ITS launch, which is a different process from this hook and may have had a
        # different environment. Measured 2026-09-09: client-launch.log exists in BOTH locations, the plugin-data one
        # live and the LOCALAPPDATA one six weeks stale, and reading only the second is what made a working code path
        # look dead to a whole fleet. So try both and take whichever actually holds this endpoint's log; guessing one
        # would produce a confident "no solution/open" from a file that was simply not the daemon's.
        $dataDirs = @()
        if ($env:CLAUDE_PLUGIN_DATA) { $dataDirs += (Join-Path $env:CLAUDE_PLUGIN_DATA 'roslyn') }
        $dataDirs += (Join-Path $env:LOCALAPPDATA 'claude-roslyn-lsp')
        $dataDirs += (Join-Path $env:USERPROFILE '.claude\plugins\data\roslyn-claude-roslyn-lsp\roslyn')

        # The daemon carries --root on its command line, so this is a READ, not a guess at which daemon is ours.
        #
        # ⚠️ COMPARE THE WHOLE ROOT, NOT A PREFIX. A wildcard match on "*--root <ws>*" also matches every daemon
        # rooted BELOW ours — measured here: the daemon at C:\SOURCE\scratch\___\echo and the one at
        # C:\SOURCE\scratch\___\echo\wt-head\src\Reactive.Graph both satisfy the echo pattern, so which one the
        # banner named came down to enumeration order. That is the same prefix-vs-identity error that put a 1.56 GB
        # daemon under the wrong root in tonight's census. Parse the argument and compare it normalised, whole.
        $daemon = @($procs | Where-Object {
            $_.Name -eq 'dotnet.exe' -and $_.CommandLine -like '*ClaudeRoslynLsp.Daemon.dll*' -and
            ($_.CommandLine -match '--root\s+(.+?)\s*$') -and
            ((($Matches[1] -replace '\\','/').TrimEnd('/').ToLowerInvariant()) -eq $norm.TrimEnd('/'))
        }) | Select-Object -First 1

        if ($daemon) {
            $ls = @($servers | Where-Object { $_.ParentProcessId -eq $daemon.ProcessId }) | Select-Object -First 1
            if ($ls) {
                $p = Get-Process -Id $ls.ProcessId -ErrorAction SilentlyContinue
                $mb = if ($p) { [math]::Round($p.PrivateMemorySize64 / 1MB, 1) } else { -1 }
                $ageH = [math]::Round(((Get-Date) - $ls.CreationDate).TotalHours, 2)

                # Did OUR daemon load anything? Read from ITS OWN STARTUP, not from the end of the file.
                $opened = $null
                $log = $null
                foreach ($dd in $dataDirs) {
                    $cand = Join-Path $dd "daemon-$endpoint.log"
                    if (Test-Path $cand) { if (-not $log -or (Get-Item $cand).LastWriteTime -gt (Get-Item $log).LastWriteTime) { $log = $cand } }
                }
                if ($log -and (Test-Path $log)) {
                    try {
                        # 🔑 SEEK BACK TO THE LAST "daemon starting" AND READ FORWARD FROM THERE. The load outcome is
                        # written in the first few lines of a daemon's life, so the END of the file is the wrong place
                        # to look and a fixed 64 KB tail simply misses it on any daemon that has been up a while. The
                        # file also accumulates ACROSS restarts (29 starts in one measured sample), so the head is the
                        # wrong place too — it is some earlier daemon's startup, not this one's. Escalating tails,
                        # capped: the answer is within a few KB of that marker in every observed case.
                        $tail = $null
                        $fsm = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
                        try {
                            foreach ($want in @(65536, 1048576, 16777216)) {
                                $take = [Math]::Min($want, $fsm.Length)
                                $null = $fsm.Seek($fsm.Length - $take, 'Begin')
                                $bufm = New-Object byte[] $take
                                $null = $fsm.Read($bufm, 0, $take)
                                $chunk = [Text.Encoding]::UTF8.GetString($bufm)
                                $cut = $chunk.LastIndexOf('daemon starting')
                                if ($cut -ge 0) { $tail = $chunk.Substring($cut); break }
                                if ($take -eq $fsm.Length) { break }        # whole file read, marker genuinely absent
                            }
                        } finally { $fsm.Close() }
                        if ($null -eq $tail) { throw 'no startup marker in the bounded read' }

                        # 🩸 THE OBVIOUS PROBE IS A FALSE POSITIVE AND I SHIPPED IT FOR ABOUT FOUR MINUTES.
                        # Matching /solution.open/ finds the token inside the line that says it did NOT happen:
                        #     "no workspace target — solution/open skipped"
                        # so a project-less daemon reported "it HAS opened a solution". Caught only because the test
                        # had a third arm — two arms (real solution, no daemon) both passed. A probe whose positive
                        # string is a SUBSTRING of its own negative case is not a probe.
                        #
                        # ⚖️ THE DAEMON ALREADY DISTINGUISHES ALL THREE STATES IN WORDS, so read those instead of
                        # inferring. PROJECTLESS is checked FIRST because it is the unambiguous one, and because it
                        # is the state a size-based reading gets exactly backwards: ~100 MB there is the CORRECT
                        # cost of single-file analysis, not evidence of a broken server.
                        #
                        # 🔴 AND A SIZE THRESHOLD MUST NOT BE THE VERDICT — the four-arm test caught that one too.
                        # I had "under 500 MB with projects present ⇒ cold". Arm 4 is the claude-roslyn-lsp repo:
                        # 205 MB, and its own log says "no solution found; opening 7 project(s)". A HEALTHY server
                        # that had loaded everything there is to load, which the threshold would have told its agent
                        # to abandon the session over. A small solution is small. There is no size that separates
                        # "loaded a little" from "loaded nothing", so this reads the OUTCOME instead of guessing at it.
                        #
                        # ⚖️ AND "OPENED" HAS TWO SPELLINGS. A root with .csproj but no .sln still loads —
                        # "project/open → 7" — so matching only solution/open calls that daemon cold as well.
                        $arrow = [char]0x2192
                        if ($tail -match 'no workspace target|no \.slnx/\.sln/\.csproj') { $opened = 'projectless' }
                        elseif ($tail.Contains("solution/open $arrow") -or $tail.Contains("project/open $arrow")) { $opened = 'opened' }
                        elseif ($tail -match 'daemon ready for clients')  { $opened = 'ready-but-unstated' }
                        else { $opened = 'never-finished' }
                    } catch { $opened = $null }
                }

                # 🔑 THE VERDICT IS THE DAEMON'S OWN STATEMENT OF WHAT IT DID. No thresholds, no inference from
                # memory, no tree walk — each branch below quotes a state the daemon wrote about itself, so the
                # instrument cannot be wrong in a way the daemon was not already wrong about.
                $verdict =
                    if ($opened -eq 'projectless') {
                        "and its startup says NO PROJECT FILE EXISTS under this root, so it serves single-file analysis only — ${mb} MB is the CORRECT cost of that job, not a cold server. workspaceSymbol has nothing to search here"
                    }
                    elseif ($opened -eq 'opened') {
                        "and its startup says it LOADED this workspace, so a timeout here is your CLIENT, not the server — see below"
                    }
                    elseif ($opened -eq 'never-finished') {
                        "and its startup NEVER REACHED 'daemon ready for clients' — that is the cold-server shape: every call of yours will time out, retrying reconnects to the SAME server, and a NEW SESSION is the only cure. grep with its scope stated is the honest fallback until then"
                    }
                    elseif ($opened -eq 'ready-but-unstated') {
                        "and its startup reached ready but did not state a load outcome — treat ${mb} MB as unexplained rather than as a verdict"
                    }
                    else {
                        "and its log could not be read, so nothing is claimed about what it loaded"
                    }
                $mine = "YOUR server (the one THIS workspace's daemon owns, pid $($ls.ProcessId)): $mb MB committed, up $ageH h — $verdict."
            }
            else { $mine = 'YOUR daemon is running but has NOT started a language server yet — the first call starts it.' }
        }
    } catch { $mine = $null }
    if (-not $mine) { $mine = 'Could not identify which server belongs to this workspace, so nothing below is claimed about YOURS specifically.' }

    $banner = @"
[Roslyn LSP] C# language server, solution-wide and self-warming. $serverLine $state
$mine

PREFER the LSP over grep for any symbol / reference / type question — it is semantic (no string false-positives) and solution-wide:
- workspaceSymbol  — find a type or member by name across the WHOLE solution (the thing to reach for instead of grepping for a definition)
- findReferences   — exact callers / blast-radius before editing a shared type (grep under- and over-counts)
- goToDefinition / goToImplementation / typeDefinition  — navigate, incl. across the parallel-interface sprawl
- hover            — signature + XML-doc summary
- get_diagnostics  — current compiler + analyzer diagnostics for a file WITHOUT a build

FIRST-CALL COLD EDGE: in a brand-new session the very first call can return "server is starting / has not finished indexing" — retry once, it warms in seconds; do not conclude the LSP is unreliable. A "no symbols found" result is a true empty answer only once indexed — if unsure, query a symbol you know exists to confirm the index is live before trusting a negative.

IF YOUR CALLS TIME OUT, IT IS PROBABLY YOUR CLIENT, NOT THE DAEMON — measured 2026-09-09, four agents, one daemon: two sessions had every call time out at 120s while two others were served normally by that same daemon. The known fault is per-session ("client N read loop ended (NullReferenceException)" strands one client; the server keeps serving everyone else), so:
- THERE ARE TWO DIFFERENT CAUSES AND THE CURES ARE OPPOSITE. (a) A WEDGED CLIENT: a timed-out call now drops its cached connection, so the NEXT call reconnects — just retry. Before 2026-09-09 it did not (the host's timeout fired before the client's own ceiling, the drop was skipped, one wedged link poisoned the session), and on older bits there is no in-session cure because MCP attaches at SESSION START. (b) A COLD SERVER: your MCP host has its OWN server, and if that one never loaded the workspace it would time out on EVERY call, forever. Retrying reconnects you to the SAME host and therefore the same cold server, so it cannot help. ⚠️ BUT DO NOT REACH FOR (b) FIRST: the line at the top of this banner has already checked, and when this was measured properly on 2026-09-09 the cold-server population was ZERO — the "two servers that never loaded anything" reading was three agents mistaking a SMALL workspace for an unloaded one (361 MB was 64 projects; 204 MB was 7). (a) is the cause that has actually been observed.
- TO TELL THEM APART: THE LINE AT THE TOP OF THIS BANNER ALREADY DID IT. It names your workspace's own server by pid, its committed memory, its age, and whether its daemon log shows a solution/open. Do not re-derive that from the process table, and do NOT judge by size alone — a daemon serving a folder of loose .cs files with no project correctly holds ~100 MB forever, because without a project Roslyn can only do single-file analysis. "Small" is not the symptom; "small while a solution was opened" is. If yours is the cold one, a new session is the only cure and grep with its scope stated is the honest fallback until then.
- It is NOT reaping the daemon. Reaping discards a multi-GB warm index that is serving other sessions.
- DO NOT diagnose the daemon by CPU. It is request/response, so 0% between requests is its CORRECT state — "flat CPU while elapsed climbs = wedged" is a rule for work that should be CONTINUOUS (a replay, a build, a boot) and it inverts here. An idle server and a wedged one look identical; only sending a request tells them apart.
- Before concluding anything about the daemon, ask whether ANOTHER session is being served. If yes, the daemon is fine and the fault is yours to reset.
"@

    Emit $banner
} catch {
    # Never break the session over a banner.
}
