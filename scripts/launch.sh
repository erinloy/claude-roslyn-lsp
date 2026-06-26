#!/usr/bin/env bash
# claude-roslyn-lsp launcher.
#
# Builds the .NET bridge from source on the device (first run, or whenever sources change), then EXECs it as the LSP
# server. The bridge then acquires Microsoft's Roslyn language server and proxies it.
#
# CRITICAL: stdout is the LSP JSON-RPC wire — nothing but the server may write to it. All build output is redirected to
# stderr (1>&2). The final `exec` replaces this shell so the bridge inherits the real stdin/stdout/stderr.
set -euo pipefail

ROOT="${CLAUDE_PLUGIN_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
DATA="${CLAUDE_PLUGIN_DATA:-$ROOT/.data}"
SRC="$ROOT/bridge"
OUT="$DATA/bridge"
DLL="$OUT/ClaudeRoslynLsp.Bridge.dll"

mkdir -p "$OUT"

needs_build=0
if [ ! -f "$DLL" ]; then
  needs_build=1
elif [ -n "$(find "$SRC" -name '*.cs' -newer "$DLL" 2>/dev/null)" ]; then
  needs_build=1
elif [ "$SRC/ClaudeRoslynLsp.Bridge.csproj" -nt "$DLL" ]; then
  needs_build=1
fi

if [ "$needs_build" -eq 1 ]; then
  echo "claude-roslyn-lsp: building bridge from source…" 1>&2
  dotnet publish "$SRC/ClaudeRoslynLsp.Bridge.csproj" -c Release -o "$OUT" 1>&2
fi

exec dotnet "$DLL" --stdio
