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

# A suspended managed process can stop every CLR thread during an alt-tab while
# its authenticated loopback connection remains alive.  The native compositor
# must retain that live connection despite a stale file, then exit promptly once
# the connection actually closes and the owner remains stale.
printf '%s 0 0 0 800 600\n' "$(date +%s%3N)" > "$HEARTBEAT"
PORT=$((PORT + 1))
xvfb-run -a -s '-screen 0 1280x720x24 +extension GLX +render -noreset' bash -c '
  set -euo pipefail
  helper=$1 heartbeat=$2 port=$3 token=$4
  "$helper" 0 0 800 600 30 "$heartbeat" "$port" "$token" >/dev/null 2>&1 &
  pid=$!
  trap "kill $pid 2>/dev/null || true; wait $pid 2>/dev/null || true" EXIT
  python3 - "$heartbeat" "$port" "$token" "$pid" <<"PY"
import os
import socket
import struct
import sys
import time

heartbeat, port, token, pid = sys.argv[1], int(sys.argv[2]), bytes.fromhex(sys.argv[3]), int(sys.argv[4])
deadline = time.time() + 5
while True:
    try:
        client = socket.create_connection(("127.0.0.1", port), 0.2)
        break
    except OSError:
        if time.time() >= deadline:
            raise
        time.sleep(0.02)

auth = struct.pack("<I", 0x31485541) + token
client.sendall(struct.pack("<I", len(auth)) + auth)
assert client.recv(4) == struct.pack("<I", 0x31594452)
with open(heartbeat, "w", encoding="utf-8") as stream:
    stream.write("1 0 0 0 800 600\n")

time.sleep(3.5)
os.kill(pid, 0)
client.close()
time.sleep(2)
os.kill(pid, 0)
with open(heartbeat, "w", encoding="utf-8") as stream:
    stream.write("-1 0 0 0 0 0\n")
deadline = time.time() + 0.5
while time.time() < deadline:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        break
    time.sleep(0.01)
else:
    raise AssertionError("explicit stop heartbeat did not bypass reconnect grace")
PY
' bash "$BINARY" "$HEARTBEAT" "$PORT" "$TOKEN" || fail "authenticated reconnect grace did not retain the compositor"

grep -q '#define AUTHENTICATED_RECONNECT_GRACE_SECONDS 300.0' "$SOURCE" || fail "authenticated reconnect grace is not explicitly bounded"

printf 'PASS: heartbeat handles torn writes, stale startup owners, and bounded authenticated reconnect grace\n'
