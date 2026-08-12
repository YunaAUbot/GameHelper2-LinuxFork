#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="$ROOT/renderer-src/gamehelper2-gpu-overlay.c"
BINARY="$(mktemp /tmp/gamehelper2-gpu-heartbeat.XXXXXX)"
HEARTBEAT="$(mktemp /tmp/gamehelper2-heartbeat-file.XXXXXX)"
TOKEN=0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF
PORT=$((42000 + $$ % 18000))

cleanup() { rm -f -- "$BINARY" "$HEARTBEAT"; }
trap cleanup EXIT
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

[[ -f "$SOURCE" ]] || fail "native GPU renderer source is missing"
command -v Xvfb >/dev/null 2>&1 || { printf 'SKIP: Xvfb is required for heartbeat lifecycle smoke\n'; exit 0; }
read -r -a cflags <<< "$(pkg-config --cflags x11 xext xrender gl)"
read -r -a libs <<< "$(pkg-config --libs x11 xext xrender gl)"
cc -Wall -Wextra -Werror -O2 "$SOURCE" "${cflags[@]}" "${libs[@]}" -lm -o "$BINARY"
printf '%s 0 0 0 800 600\n' "$(date +%s%3N)" > "$HEARTBEAT"

# The single-quoted body is an intentionally isolated child shell script.
# shellcheck disable=SC2016
xvfb-run -a -s '-screen 0 1280x720x24 +extension GLX +render -noreset' bash -c '
  set -euo pipefail
  helper=$1 heartbeat=$2 port=$3 token=$4
  "$helper" 0 0 800 600 30 "$heartbeat" "$port" "$token" >/dev/null 2>&1 &
  pid=$!
  trap "kill $pid 2>/dev/null || true; wait $pid 2>/dev/null || true" EXIT
  for _ in {1..100}; do kill -0 "$pid" 2>/dev/null && break; sleep 0.01; done

  # Managed File.WriteAllText truncates before writing. A single read in that
  # tiny window must not classify the live owner as orphaned.
  : > "$heartbeat"
  sleep 0.2
  kill -0 "$pid" 2>/dev/null || exit 41
  printf "%s 0 0 0 800 600\n" "$(date +%s%3N)" > "$heartbeat"
  sleep 0.2
  kill -0 "$pid" 2>/dev/null || exit 42

  # A genuinely stale, well-formed heartbeat must still terminate promptly.
  printf "1 0 0 0 800 600\n" > "$heartbeat"
  for _ in {1..100}; do
    kill -0 "$pid" 2>/dev/null || { wait "$pid" 2>/dev/null || true; exit 0; }
    sleep 0.02
  done
  exit 43
' bash "$BINARY" "$HEARTBEAT" "$PORT" "$TOKEN" || fail "renderer did not tolerate a torn heartbeat and expire a stale owner"

printf 'PASS: torn heartbeat is tolerated while a stale owner still expires\n'
