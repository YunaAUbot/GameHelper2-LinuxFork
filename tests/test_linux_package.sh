#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
SOLUTION="$ROOT/GameOverlay.Linux.sln"
BUILDER="$ROOT/build-linux-package.sh"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

[[ -f "$SOLUTION" ]] || fail "Linux passive-plugin solution is missing"
[[ -x "$BUILDER" ]] || fail "Linux package builder is missing or not executable"

if "$BUILDER" / >/dev/null 2>&1; then
  fail "builder accepts the filesystem root as output"
fi
if "$BUILDER" "$ROOT" >/dev/null 2>&1; then
  fail "builder accepts the repository root as output"
fi

python3 "$ROOT/tests/test_linux_plugin_discovery.py"
grep -q 'discover-linux-plugins.py' "$BUILDER" || fail "builder does not discover plugin projects"
grep -q 'EnableWindowsTargeting=true' "$BUILDER" || fail "Linux build does not enable Windows targeting"
! grep -Eq '^for forbidden.*(AutoHotKeyTrigger|PickupHelper)' "$BUILDER" || fail "builder still rejects discovered plugins"

grep -q 'price_cache.json' "$BUILDER" || fail "builder does not remove and reject NinjaPricer runtime cache"
grep -q 'price_cache.json.tmp-' "$BUILDER" || fail "builder does not remove and reject NinjaPricer temporary cache files"
grep -Fq 'dotnet=${DOTNET:-dotnet}' "$BUILDER" || fail "builder does not use the caller's portable dotnet command"
! grep -q '/root/.dotnet' "$BUILDER" || fail "builder contains a host-specific dotnet path"
grep -q 'publish.*GameHelper' "$BUILDER" || fail "builder does not publish GameHelper"
grep -q -- '--self-contained true' "$BUILDER" || fail "publish is not self-contained"
grep -q 'GenerateDocumentationFile=false' "$BUILDER" || fail "known publish XML documentation failure is not disabled"
grep -q 'pkg-config.*x11.*xext.*xrender.*gl' "$BUILDER" || fail "native GPU helper dependencies are not explicit"
grep -q 'gamehelper2-gpu-overlay' "$BUILDER" || fail "native GPU helper is not staged"
grep -q 'README-LINUX.md' "$BUILDER" || fail "builder does not stage Linux operating documentation"

printf 'PASS: Linux package builder is self-contained and packages discovered plugins\n'
