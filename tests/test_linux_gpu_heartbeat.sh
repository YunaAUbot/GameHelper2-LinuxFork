#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="$ROOT/renderer-src/gamehelper2-gpu-overlay.c"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

[[ -f "$SOURCE" ]] || fail "native GPU renderer source is missing"
grep -Fq 'static double heartbeat_timestamp_seconds(const char *path)' "$SOURCE" ||
  fail "renderer does not parse the managed monotonic heartbeat timestamp"
grep -Fq 'wall_seconds()-heartbeat_timestamp>3' "$SOURCE" ||
  fail "renderer still relies on coarse filesystem mtime for owner liveness"

printf 'PASS: native renderer exits from the managed monotonic heartbeat\n'
