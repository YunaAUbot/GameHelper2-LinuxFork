#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

plugin="$repo_root/Plugins/NinjaPricer"
test -f "$plugin/NinjaPricer.csproj"
test -f "$plugin/AGENTS.md"
test -f "$plugin/LICENSE"
test -f "$plugin/README.md"

grep -Fq 'Plugins\NinjaPricer\NinjaPricer.csproj' "$repo_root/GameOverlay.sln"
grep -Fq 'NinjaPricer' "$repo_root/README-LINUX.md"

if grep -RIEq '(gh[pousr]_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY)' "$plugin" \
    --exclude-dir=bin --exclude-dir=obj --exclude-dir=config; then
  echo "NinjaPricer bundle contains credential-like material" >&2
  exit 1
fi

echo "NinjaPricer bundle contract: PASS"
