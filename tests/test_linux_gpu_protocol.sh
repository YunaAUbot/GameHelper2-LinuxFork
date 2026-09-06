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

# Zero is a valid unlimited lifetime, but malformed/non-finite/negative values
# must not be silently promoted to unlimited operation.
for invalid_duration in invalid nan inf -1; do
  set +e
  "$BINARY" 0 0 800 600 "$invalid_duration" "$HEARTBEAT" "$PORT" "$TOKEN" >/dev/null 2>&1
  status=$?
  set -e
  [[ $status -eq 2 ]] || {
    printf 'FAIL: invalid duration %s returned %s instead of 2\n' "$invalid_duration" "$status" >&2
    exit 1
  }
done

# The single-quoted body is an intentionally isolated child shell script.
# shellcheck disable=SC2016
xvfb-run -a -s '-screen 0 1280x720x24 +extension GLX +render -noreset' bash -c '
  set -euo pipefail
  helper=$1 heartbeat=$2 port=$3 token=$4
  # Duration 0 is the production no-hard-expiry mode. The authenticated
  # shutdown frame and owner heartbeat still bound orphan lifetime.
  "$helper" 0 0 800 600 0 "$heartbeat" "$port" "$token" \
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
TEXTURE = 0x31585445
TEXTURE_DELETE = 0x31445445
TEXTURE_ACK = 0x314B5458
FRAME = 0x31464745
INPUT = 0x31435345
SHUTDOWN = 0x31545845


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


def recv_exact(sock, count):
    chunks = []
    while count:
        chunk = sock.recv(count)
        if not chunk:
            raise EOFError("socket closed before texture acknowledgement")
        chunks.append(chunk)
        count -= len(chunk)
    return b"".join(chunks)


def await_texture_ack(sock, operation, texture, success):
    sock.settimeout(2)
    while True:
        size = struct.unpack("<I", recv_exact(sock, 4))[0]
        message = recv_exact(sock, size)
        if len(message) != 20 or struct.unpack_from("<I", message)[0] != TEXTURE_ACK:
            continue
        _, got_operation, got_success, low, high = struct.unpack("<IIIII", message)
        assert (got_operation, low | (high << 32), got_success) == (operation, texture, success)
        sock.settimeout(None)
        return


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

# A bounded RGBA texture can be uploaded, referenced by its 64-bit logical ID,
# deleted, and then safely ignored when a later frame still references it.
texture_id = 2
pixels = bytes((255, 0, 0, 255) * 4)
def assert_no_texture_ack(sock, duration):
    deadline = time.time() + duration
    sock.settimeout(duration)
    try:
        while time.time() < deadline:
            try:
                size = struct.unpack("<I", recv_exact(sock, 4))[0]
                message = recv_exact(sock, size)
            except TimeoutError:
                return
            assert len(message) != 20 or struct.unpack_from("<I", message)[0] != TEXTURE_ACK
    finally:
        sock.settimeout(None)


payload = struct.pack("<IQIIIII", TEXTURE, texture_id, 2, 2, len(pixels), 0, 8) + pixels[:8]
client.sendall(struct.pack("<I", len(payload)) + payload)
assert_no_texture_ack(client, 0.05)
payload = struct.pack("<IQIIIII", TEXTURE, texture_id, 2, 2, len(pixels), 8, 8) + pixels[8:]
client.sendall(struct.pack("<I", len(payload)) + payload)
await_texture_ack(client, 1, texture_id, 1)

verts = b"".join(
    struct.pack("<ffffBBBB", x, y, u, v, 255, 255, 255, 255)
    for x, y, u, v in ((10, 10, 0, 0), (20, 10, 1, 0), (10, 20, 0, 1))
)
indices = struct.pack("<HHH", 0, 1, 2)
command = struct.pack("<IIIffffQ", 3, 0, 0, 0.0, 0.0, 800.0, 600.0, texture_id)
payload = struct.pack("<IIIIffffIII", FRAME, 3, 3, 1, 0.0, 0.0, 800.0, 600.0, 3, 3, 1) + verts + indices + command
client.sendall(struct.pack("<I", len(payload)) + payload)

payload = struct.pack("<IQ", TEXTURE_DELETE, texture_id)
client.sendall(struct.pack("<I", len(payload)) + payload)
await_texture_ack(client, 2, texture_id, 1)
# Delete is idempotent so an absent/rejected in-flight upload cannot retry forever.
client.sendall(struct.pack("<I", len(payload)) + payload)
await_texture_ack(client, 2, texture_id, 1)
client.sendall(struct.pack("<I", len(payload := struct.pack("<IIIIffffIII", FRAME, 3, 3, 1, 0.0, 0.0, 800.0, 600.0, 3, 3, 1) + verts + indices + command)) + payload)

# Oversized dimensions are rejected without allocating or terminating GLX.
payload = struct.pack("<IQIIIII", TEXTURE, 3, 4097, 1, 0, 0, 0)
client.sendall(struct.pack("<I", len(payload)) + payload)
await_texture_ack(client, 1, 3, 0)

payload = struct.pack(
    "<IIIIffff", FRAME, 0xFFFFFFFF, 0xFFFFFFFF, 0, math.nan, 0.0, 800.0, 600.0
)
client.sendall(struct.pack("<I", len(payload)) + payload)
time.sleep(0.15)
os.kill(pid, 0)
client.close()

# Partial texture assembly is scoped to one authenticated connection.
time.sleep(0.1)
first = connect_retry()
authenticate(first, token)
assert first.recv(4) == struct.pack("<I", READY)
pixels = bytes((9, 8, 7, 6) * 4)
payload = struct.pack("<IQIIIII", TEXTURE, 4, 2, 2, len(pixels), 0, 8) + pixels[:8]
first.sendall(struct.pack("<I", len(payload)) + payload)
first.close()
time.sleep(0.1)
second = connect_retry()
authenticate(second, token)
assert second.recv(4) == struct.pack("<I", READY)
payload = struct.pack("<IQIIIII", TEXTURE, 4, 2, 2, len(pixels), 8, 8) + pixels[8:]
second.sendall(struct.pack("<I", len(payload)) + payload)
await_texture_ack(second, 1, 4, 0)
second.close()

# A suspended authenticated sender may stop halfway through one bounded payload.
# Drop only that TCP connection, keep the compositor alive, and accept a newly
# authenticated stream whose first frame has an unambiguous boundary.
time.sleep(0.1)
partial = connect_retry()
authenticate(partial, token)
assert partial.recv(4) == struct.pack("<I", READY)
payload = struct.pack("<II", INPUT, 1)
partial.sendall(struct.pack("<I", len(payload)) + payload[:2])
time.sleep(1.2)
partial.settimeout(0.1)
closed = False
for _ in range(20):
    try:
        if partial.recv(4096) == b"":
            closed = True
            break
    except ConnectionResetError:
        closed = True
        break
    except TimeoutError:
        pass
    time.sleep(0.02)
partial.close()
assert closed
os.kill(pid, 0)

resumed = connect_retry()
authenticate(resumed, token)
assert resumed.recv(4) == struct.pack("<I", READY)
resumed.sendall(struct.pack("<I", len(payload)) + payload)
time.sleep(0.2)
assert "mode 1" in open("/tmp/gamehelper2-gpu-input.log", encoding="utf-8").read()
resumed.close()
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

# A deliberate authenticated shutdown must remove the native window promptly;
# reconnect grace is only for accidental transport loss.
time.sleep(0.1)
stop_client = connect_retry()
authenticate(stop_client, token)
assert stop_client.recv(4) == struct.pack("<I", READY)
payload = struct.pack("<I", SHUTDOWN)
stop_client.sendall(struct.pack("<I", len(payload)) + payload)
stop_client.close()
deadline = time.time() + 0.5
while time.time() < deadline:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        break
    time.sleep(0.01)
else:
    raise AssertionError("authenticated shutdown left the native overlay visible")
print("PASS: native auth, texture lifecycle, reconnect, and prompt shutdown smoke")
PY
' bash "$BINARY" "$HEARTBEAT" "$PORT" "$TOKEN"
