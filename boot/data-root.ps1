<#
  data-root.ps1 — the ONE PowerShell resolver for this plugin's data root. Dot-source it, then call it:

      . (Join-Path $PSScriptRoot 'data-root.ps1')
      $dataRoot = Resolve-PluginDataRoot

  Its C# twin is shared/PluginDataRoot.cs (linked into every host project); the two MUST answer identically, so a change
  here is a change there. PRECEDENCE, first match wins:
    1. CLAUDE_PLUGIN_DATA (Claude Code's per-plugin data dir) -> <it>\roslyn   (unchanged: the layout already used under it)
    2. ZILTCH_DATA_ROOT                                       -> <it>\claude-roslyn-lsp
    3. Z:\DATA, when that directory exists                    -> Z:\DATA\claude-roslyn-lsp
    4. otherwise THROW, naming the variables.
  Null, empty and whitespace-only values mean ABSENT. Branches 2-4 mirror the Ziltch repo's ZiltchDataRoot.Resolve.

  NO APPDATA, AND NO OTHER FALLBACK (Erin, 2026-09-17: "get that kind of stuff out of appdata"). Every script here used to
  write under %LOCALAPPDATA%\claude-roslyn-lsp; that morning a git-bash `rm -rf` of a backslash path wiped the whole of
  %LOCALAPPDATA%. A missing data root is a configuration error, not a location to guess.

  Paths are composed with [System.IO.Path]::Combine, NOT Join-Path: Join-Path resolves the drive through the provider and
  fails on one that does not exist, which would make this function throw a different error than its C# twin for the same
  input (a ZILTCH_DATA_ROOT on an unmounted drive).

  -DirectoryExists is injectable so every branch, including the throw, is testable on a machine where Z:\DATA exists.
#>
function Resolve-PluginDataRoot {
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()][string]$PluginData = $env:CLAUDE_PLUGIN_DATA,
        [AllowNull()][AllowEmptyString()][string]$ZiltchDataRoot = $env:ZILTCH_DATA_ROOT,
        [scriptblock]$DirectoryExists = { param([string]$Path) Test-Path -LiteralPath $Path -PathType Container }
    )

    if (-not [string]::IsNullOrWhiteSpace($PluginData)) {
        return [System.IO.Path]::Combine($PluginData, 'roslyn')
    }
    if (-not [string]::IsNullOrWhiteSpace($ZiltchDataRoot)) {
        return [System.IO.Path]::Combine($ZiltchDataRoot, 'claude-roslyn-lsp')
    }
    $preferred = 'Z:\DATA'
    if (& $DirectoryExists $preferred) {
        return [System.IO.Path]::Combine($preferred, 'claude-roslyn-lsp')
    }
    throw [System.InvalidOperationException]::new(
        "No claude-roslyn-lsp data root: CLAUDE_PLUGIN_DATA and ZILTCH_DATA_ROOT are unset and $preferred does not exist. " +
        "Set ZILTCH_DATA_ROOT to a data directory (or mount Z:); Claude Code sets CLAUDE_PLUGIN_DATA for the processes it " +
        "launches. There is deliberately no AppData or temp fallback.")
}
