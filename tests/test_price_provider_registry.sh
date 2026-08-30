#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-/root/.dotnet/dotnet}"
"$DOTNET" run --project "$ROOT/tests/PriceProviderRegistryProbe/PriceProviderRegistryProbe.csproj" -c Release --no-restore -p:BuildInParallel=false -m:1
