#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="$ROOT/GameHelper/RemoteObjects/States/InGameStateObjects/ImportantUiElements.cs"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

[[ -f "$SOURCE" ]] || fail "ImportantUiElements source is missing"

grep -Fq 'private readonly UiElementParents atlasCache;' "$SOURCE" ||
  fail "atlas UI parents do not have a dedicated cache"
grep -Fq 'this.atlasCache = new(this.rootCache' "$SOURCE" ||
  fail "atlas cache is not attached beneath the stable root cache"
grep -Fq 'this.Atlas = new(IntPtr.Zero, this.atlasCache);' "$SOURCE" ||
  fail "Atlas children still pollute the per-frame root parent cache"
grep -Fq 'if (this.Atlas.IsVisible)' "$SOURCE" ||
  fail "atlas parent refresh is not visibility-gated"
grep -Fq 'this.atlasCache.UpdateAllParentsParallel();' "$SOURCE" ||
  fail "visible atlas parents are not refreshed"

clear_count="$(grep -Fc 'this.atlasCache.Clear();' "$SOURCE")"
(( clear_count >= 2 )) ||
  fail "atlas cache is not cleared on both panel close and object cleanup"

printf 'PASS: transient atlas parents are isolated and cleared when hidden\n'
