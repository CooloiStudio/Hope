#!/usr/bin/env bash
# Hope — 一键跑全量单测（Git Bash / Linux CI 本地）
#
# 用法：
#   ./scripts/test.sh
#   ./scripts/test.sh --desktop-only
#   ./scripts/test.sh --headless-only
#   ./scripts/test.sh --configuration Debug

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DESKTOP_ONLY=0
HEADLESS_ONLY=0
CONFIGURATION=Release

while [[ $# -gt 0 ]]; do
  case "$1" in
    --desktop-only) DESKTOP_ONLY=1; shift ;;
    --headless-only) HEADLESS_ONLY=1; shift ;;
    --configuration) CONFIGURATION="$2"; shift 2 ;;
    -h|--help)
      echo "Usage: $0 [--desktop-only] [--headless-only] [--configuration Release|Debug]"
      exit 0
      ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

FAILED=0

if [[ "$DESKTOP_ONLY" -eq 0 ]]; then
  echo ""
  echo "==> headless: go test ./..."
  (cd "$ROOT/src/headless" && go test ./...) || FAILED=1
fi

if [[ "$HEADLESS_ONLY" -eq 0 ]]; then
  echo ""
  echo "==> desktop: dotnet test ($CONFIGURATION)"
  DOTNET_BIN="${DOTNET_BIN:-dotnet}"
  if ! command -v "$DOTNET_BIN" >/dev/null 2>&1; then
    if [[ -x "/c/Program Files/dotnet/dotnet.exe" ]]; then
      DOTNET_BIN="/c/Program Files/dotnet/dotnet.exe"
    else
      echo "dotnet not found" >&2
      exit 1
    fi
  fi
  "$DOTNET_BIN" test "$ROOT/src/win-desktop/tests/Hope.Desktop.Tests.csproj" -c "$CONFIGURATION" --verbosity minimal || FAILED=1
fi

echo ""
if [[ "$FAILED" -ne 0 ]]; then
  echo "==> FAIL"
  exit 1
fi
echo "==> PASS"
