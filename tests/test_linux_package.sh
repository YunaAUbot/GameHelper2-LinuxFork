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

for project in \
  'GameHelper\\GameHelper.csproj' \
  'GameOffsets\\GameOffsets.csproj' \
  'renderer-src\\ClickableTransparentOverlay\\ClickableTransparentOverlay\\ClickableTransparentOverlay.csproj' \
  'Plugins\\PreloadAlert\\PreloadAlert.csproj' \
  'Plugins\\Radar\\Radar.csproj' \
  'Plugins\\HealthBars\\HealthBars.csproj' \
  'Plugins\\Atlas2\\Atlas2.csproj' \
  'Plugins\\PlayerBuffBar\\PlayerBuffBar.csproj' \
  'Plugins\\LootValue\\LootValue.csproj' \
  'Plugins\\NinjaPricer\\NinjaPricer.csproj'; do
  grep -Fq "$project" "$SOLUTION" || fail "Linux solution omits passive project: $project"
done

for forbidden in AutoHotKeyTrigger PickupHelper Launcher; do
  ! grep -q "$forbidden" "$SOLUTION" || fail "Linux solution includes excluded project: $forbidden"
done

grep -q 'GameOverlay.Linux.sln' "$BUILDER" || fail "builder does not build the Linux solution"
grep -q 'EnableWindowsTargeting=true' "$BUILDER" || fail "Linux build does not enable Windows targeting"
grep -Eq '^passive_plugins=.*LootValue.*NinjaPricer' "$BUILDER" || fail "builder does not package LootValue with NinjaPricer"
! grep -Eq '^for forbidden.*LootValue' "$BUILDER" || fail "builder still rejects bundled LootValue"
grep -q 'price_cache.json' "$BUILDER" || fail "builder does not remove and reject NinjaPricer runtime cache"
grep -q 'price_cache.json.tmp-' "$BUILDER" || fail "builder does not remove and reject NinjaPricer temporary cache files"
grep -q 'publish.*GameHelper' "$BUILDER" || fail "builder does not publish GameHelper"
grep -q -- '--self-contained true' "$BUILDER" || fail "publish is not self-contained"
grep -q 'GenerateDocumentationFile=false' "$BUILDER" || fail "known publish XML documentation failure is not disabled"
grep -q 'pkg-config.*x11.*xext.*xrender.*gl' "$BUILDER" || fail "native GPU helper dependencies are not explicit"
grep -q 'gamehelper2-gpu-overlay' "$BUILDER" || fail "native GPU helper is not staged"
grep -q 'README-LINUX.md' "$BUILDER" || fail "builder does not stage Linux operating documentation"

printf 'PASS: Linux package builder is self-contained and packages only approved plugins\n'
