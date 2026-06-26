#!/usr/bin/env bash
# claude-roslyn-lsp refactoring MCP launcher.
#
# Builds the .NET MCP server from source on the device (first run, or whenever sources change), then EXECs it as a
# stdio MCP server. The server drives Roslyn (MSBuildWorkspace) to perform solution-wide refactorings.
#
# CRITICAL: stdout is the MCP JSON-RPC wire — nothing but the server may write to it. All build output is redirected
# to stderr (1>&2). The final `exec` replaces this shell so the server inherits the real stdin/stdout/stderr.
set -euo pipefail

ROOT="${CLAUDE_PLUGIN_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
DATA="${CLAUDE_PLUGIN_DATA:-$ROOT/.data}"
SRC="$ROOT/mcp"
OUT="$DATA/mcp"
DLL="$OUT/ClaudeRoslynLsp.Mcp.dll"

mkdir -p "$OUT"

needs_build=0
if [ ! -f "$DLL" ]; then
  needs_build=1
elif [ -n "$(find "$SRC" -name '*.cs' -newer "$DLL" 2>/dev/null)" ]; then
  needs_build=1
elif [ "$SRC/ClaudeRoslynLsp.Mcp.csproj" -nt "$DLL" ]; then
  needs_build=1
fi

if [ "$needs_build" -eq 1 ]; then
  echo "claude-roslyn-lsp: building refactoring MCP from source…" 1>&2
  dotnet publish "$SRC/ClaudeRoslynLsp.Mcp.csproj" -c Release -o "$OUT" 1>&2
fi

exec dotnet "$DLL"
