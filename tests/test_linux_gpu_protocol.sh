#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
HELPER="$ROOT/renderer-src/gamehelper2-gpu-overlay.c"
BINARY="$(mktemp /tmp/gamehelper2-gpu-protocol.XXXXXX)"
HEARTBEAT="$(mktemp /tmp/gamehelper2-gpu-heartbeat.XXXXXX)"
TOKEN=0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF
PORT=$((40000 + $$ % 20000))

cleanup() {
  rm -f -- "$BINARY" "$HEARTBEAT"
}
trap cleanup EXIT

if ! command -v Xvfb >/dev/null 2>&1; then
  printf 'SKIP: Xvfb is required for native GPU protocol smoke\n'
  exit 0
fi

read -r -a cflags <<< "$(pkg-config --cflags x11 xext xrender gl)"
read -r -a libs <<< "$(pkg-config --libs x11 xext xrender gl)"
cc -Wall -Wextra -Werror -O2 "$HELPER" "${cflags[@]}" "${libs[@]}" -lm -o "$BINARY"
printf '%s 0 0 0 800 600\n' "$(date +%s%3N)" > "$HEARTBEAT"
rm -f /tmp/gamehelper2-gpu-input.log

# The single-quoted body is an intentionally isolated child shell script.
# shellcheck disable=SC2016
xvfb-run -a -s '-screen 0 1280x720x24 +extension GLX +render -noreset' bash -c '
  set -euo pipefail
  helper=$1 heartbeat=$2 port=$3 token=$4
  "$helper" 0 0 800 600 30 "$heartbeat" "$port" "$token" \
    >/tmp/gamehelper2-gpu-protocol.stdout 2>/tmp/gamehelper2-gpu-protocol.stderr &
  pid=$!
  trap "kill $pid 2>/dev/null || true; wait $pid 2>/dev/null || true" EXIT
  python3 - "$port" "$token" "$pid" <<"PY"
import math
import os
import socket
import struct
import sys
import time

port = int(sys.argv[1])
token = bytes.fromhex(sys.argv[2])
pid = int(sys.argv[3])
AUTH = 0x31485541
READY = 0x31594452
FONT = 0x31415445
FRAME = 0x31464745
INPUT = 0x31435345


def connect_retry():
    deadline = time.time() + 5
    while True:
        try:
            return socket.create_connection(("127.0.0.1", port), 0.2)
        except OSError:
            if time.time() >= deadline:
                raise
            time.sleep(0.02)


def authenticate(sock, candidate):
    payload = struct.pack("<I", AUTH) + candidate
    sock.sendall(struct.pack("<I", len(payload)) + payload)


bad = connect_retry()
authenticate(bad, b"x" * 32)
time.sleep(0.05)
try:
    bad.sendall(b"x")
except OSError:
    pass
bad.close()
os.kill(pid, 0)

client = connect_retry()
authenticate(client, token)
assert client.recv(4) == struct.pack("<I", READY)

# GameHelper can pause for asset/plugin initialization after the handshake.
# An idle-but-authenticated connection must remain valid instead of being
# mistaken for EOF by the native receive timeout.
time.sleep(1.5)
client.settimeout(0.1)
try:
    assert client.recv(1, socket.MSG_PEEK) != b""
except TimeoutError:
    pass
finally:
    client.settimeout(None)

payload = struct.pack("<II", INPUT, 0)
header = struct.pack("<I", len(payload))
client.sendall(header[:2])
time.sleep(0.05)
client.sendall(header[2:] + payload)

payload = struct.pack("<IIII", FONT, 0xFFFFFFFF, 2, 0)
client.sendall(struct.pack("<I", len(payload)) + payload)

payload = struct.pack(
    "<IIIIffff", FRAME, 0xFFFFFFFF, 0xFFFFFFFF, 0, math.nan, 0.0, 800.0, 600.0
)
client.sendall(struct.pack("<I", len(payload)) + payload)
time.sleep(0.15)
os.kill(pid, 0)
client.close()

# A stalled partial payload is bounded to one receive timeout, not four.
time.sleep(0.1)
partial = connect_retry()
authenticate(partial, token)
assert partial.recv(4) == struct.pack("<I", READY)
payload = struct.pack("<II", INPUT, 0)
partial.sendall(struct.pack("<I", len(payload)) + payload[:2])
time.sleep(1.2)
partial.settimeout(0.1)
closed = False
deadline = time.time() + 1.0
while time.time() < deadline:
    try:
        if partial.recv(4096) == b"":
            closed = True
            break
    except TimeoutError:
        pass
assert closed
partial.close()
os.kill(pid, 0)

# POLLIN and POLLHUP may arrive together. Drain the final complete message
# before closing so the input-mode transition is not discarded.
time.sleep(0.1)
final = connect_retry()
authenticate(final, token)
assert final.recv(4) == struct.pack("<I", READY)
payload = struct.pack("<II", INPUT, 1)
final.sendall(struct.pack("<I", len(payload)) + payload)
final.shutdown(socket.SHUT_WR)
time.sleep(0.2)
assert "mode 1" in open("/tmp/gamehelper2-gpu-input.log", encoding="utf-8").read()
final.close()
print("PASS: native auth, split-header, malformed-font and malformed-frame smoke")
PY
' bash "$BINARY" "$HEARTBEAT" "$PORT" "$TOKEN"
