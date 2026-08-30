#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-/root/.dotnet/dotnet}"
"$DOTNET" run --project "$ROOT/tests/NativeHeartbeatProbe/NativeHeartbeatProbe.csproj" --configuration Release
