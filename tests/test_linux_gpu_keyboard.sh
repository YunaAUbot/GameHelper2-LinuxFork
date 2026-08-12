#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
HELPER="$ROOT/renderer-src/gamehelper2-gpu-overlay.c"
INJECTOR_SOURCE="$ROOT/tests/keyboard_injector.c"
HELPER_BINARY="$(mktemp /tmp/gamehelper2-gpu-keyboard-helper.XXXXXX)"
INJECTOR_BINARY="$(mktemp /tmp/gamehelper2-gpu-keyboard-injector.XXXXXX)"
HEARTBEAT="$(mktemp /tmp/gamehelper2-gpu-keyboard-heartbeat.XXXXXX)"
TOKEN=ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789
PORT=$((42000 + $$ % 18000))

cleanup() {
  rm -f -- "$HELPER_BINARY" "$INJECTOR_BINARY" "$HEARTBEAT"
}
trap cleanup EXIT

if ! command -v Xvfb >/dev/null 2>&1 || ! ldconfig -p | grep -q 'libXtst\.so\.6'; then
  printf 'SKIP: Xvfb and libXtst.so.6 are required for keyboard smoke\n'
  exit 0
fi

read -r -a cflags <<< "$(pkg-config --cflags x11 xext xrender gl)"
read -r -a libs <<< "$(pkg-config --libs x11 xext xrender gl)"
cc -Wall -Wextra -Werror -O2 "$HELPER" "${cflags[@]}" "${libs[@]}" -lm -o "$HELPER_BINARY"
cc -Wall -Wextra -Werror -O2 "$INJECTOR_SOURCE" -lX11 -Wl,-l:libXtst.so.6 -o "$INJECTOR_BINARY"
printf '%s 0 0 0 800 600\n' "$(date +%s%3N)" > "$HEARTBEAT"

# shellcheck disable=SC2016
xvfb-run -a -s '-screen 0 1280x720x24 +extension GLX +extension XTEST +render -noreset' bash -c '
  set -euo pipefail
  helper=$1 injector=$2 heartbeat=$3 port=$4 token=$5
  "$helper" 0 0 800 600 30 "$heartbeat" "$port" "$token" \
    >/tmp/gamehelper2-gpu-keyboard.stdout 2>/tmp/gamehelper2-gpu-keyboard.stderr &
  pid=$!
  trap "kill $pid 2>/dev/null || true; wait $pid 2>/dev/null || true" EXIT
  python3 - "$port" "$token" "$injector" <<"PY"
import socket
import struct
import subprocess
import sys
import time

port = int(sys.argv[1])
token = bytes.fromhex(sys.argv[2])
injector = sys.argv[3]
AUTH = 0x31485541
READY = 0x31594452
KEYBOARD_MODE = 0x314B5345
KEY_INPUT = 0x314B4E45


def recv_exact(sock, count):
    data = bytearray()
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            raise RuntimeError("native keyboard transport closed")
        data.extend(chunk)
    return bytes(data)


def read_key(sock, expected, timeout=2.0):
    deadline = time.time() + timeout
    sock.settimeout(0.2)
    while time.time() < deadline:
        try:
            length = struct.unpack("<I", recv_exact(sock, 4))[0]
            payload = recv_exact(sock, length)
        except TimeoutError:
            continue
        kind, code, down, value, _ = struct.unpack("<IIIII", payload)
        if kind == KEY_INPUT and code == expected:
            return bool(down), value
    raise AssertionError(f"key 0x{expected:x} was not forwarded")


def connect_retry():
    deadline = time.time() + 5
    while True:
        try:
            return socket.create_connection(("127.0.0.1", port), 0.2)
        except OSError:
            if time.time() >= deadline:
                raise
            time.sleep(0.02)


client = connect_retry()
auth = struct.pack("<I", AUTH) + token
client.sendall(struct.pack("<I", len(auth)) + auth)
assert recv_exact(client, 4) == struct.pack("<I", READY)

# F12 must be available as a passive global hotkey while text capture is off,
# with both halves of the event forwarded.
subprocess.run([injector, "F12"], check=True)
down, codepoint = read_key(client, 0xFFC9)
assert down and codepoint == 0
down, codepoint = read_key(client, 0xFFC9)
assert not down and codepoint == 0

# Once ImGui asks for keyboard capture, ordinary text input is grabbed and
# forwarded with its Unicode codepoint instead of leaking through to PoE2.
payload = struct.pack("<II", KEYBOARD_MODE, 1)
client.sendall(struct.pack("<I", len(payload)) + payload)
time.sleep(0.1)
subprocess.run([injector, "a"], check=True)
down, codepoint = read_key(client, ord("a"))
assert down and codepoint == ord("a")
down, codepoint = read_key(client, ord("a"))
assert not down and codepoint == 0

# Turning capture off while a helper-observed key is held must synthesize its
# release before XUngrabKeyboard, preventing managed key state from sticking.
subprocess.run([injector, "b", "down"], check=True)
down, codepoint = read_key(client, ord("b"))
assert down and codepoint == ord("b")
payload = struct.pack("<II", KEYBOARD_MODE, 0)
client.sendall(struct.pack("<I", len(payload)) + payload)
down, codepoint = read_key(client, ord("b"))
assert not down and codepoint == 0

# An authenticated disconnect releases an active keyboard grab immediately.
client.sendall(struct.pack("<I", 8) + struct.pack("<II", KEYBOARD_MODE, 1))
time.sleep(0.1)
client.close()
deadline = time.time() + 1.0
while True:
    result = subprocess.run([injector, "--grab-keyboard"], check=False)
    if result.returncode == 0:
        break
    if time.time() >= deadline:
        raise AssertionError("keyboard grab remained active after authenticated disconnect")
    time.sleep(0.02)
print("PASS: F12/text down-up, synthetic release, and immediate disconnect ungrab")
PY
' bash "$HELPER_BINARY" "$INJECTOR_BINARY" "$HEARTBEAT" "$PORT" "$TOKEN"
