#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d /tmp/gamehelper2-package-replacement.XXXXXX)"
OUTPUT="$WORK/package"
cleanup() { rm -rf -- "$WORK"; }
trap cleanup EXIT

mkdir -p "$OUTPUT"
printf 'previous package\n' > "$OUTPUT/sentinel"

set +e
GH2_PACKAGE_TEST_ABORT_AFTER_BACKUP=1 "$ROOT/build-linux-package.sh" "$OUTPUT" \
  >"$WORK/stdout" 2>"$WORK/stderr"
status=$?
set -e

[[ "$status" -eq 97 ]] || {
  printf 'FAIL: expected simulated interruption exit 97, got %s\n' "$status" >&2
  exit 1
}
[[ "$(<"$OUTPUT/sentinel")" == 'previous package' ]] || {
  printf 'FAIL: previous package was not restored after interruption\n' >&2
  exit 1
}
if find "$WORK" -maxdepth 1 -type d -name '.GameHelper2-previous.*' -print -quit | grep -q .; then
  printf 'FAIL: previous-package staging directory leaked\n' >&2
  exit 1
fi

printf 'PASS: interrupted package replacement restores the previous artifact\n'
