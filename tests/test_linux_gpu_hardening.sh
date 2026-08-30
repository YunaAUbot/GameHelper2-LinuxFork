#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
HELPER="$ROOT/renderer-src/gamehelper2-gpu-overlay.c"
CTO="$ROOT/renderer-src/ClickableTransparentOverlay/ClickableTransparentOverlay"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
require() { grep -Eq "$1" "$2" || fail "$3"; }

require 'AUTH_MAGIC' "$HELPER" 'native helper has no authentication handshake'
require 'READY_MAGIC' "$HELPER" 'native helper does not acknowledge authenticated readiness'
require 'AUTH_TOKEN_BYTES' "$HELPER" 'native helper does not validate a fixed-size launch token'
require 'offset[[:space:]]*>[[:space:]]*ni|elems[[:space:]]*>[[:space:]]*ni[[:space:]]*-[[:space:]]*offset' "$HELPER" \
  'draw command range check is not overflow-safe'
require 'SIZE_MAX' "$HELPER" 'native parser lacks size overflow guards'
require 'isfinite' "$HELPER" 'native parser does not reject non-finite geometry/scissors'
require 'fw.*UINT32_MAX.*fh|fh.*UINT32_MAX.*fw' "$HELPER" 'font dimension multiplication is not guarded'
require 'XDestroyRegion' "$HELPER" 'frame parser does not clean up its input region'
require 'draw_frame\([^)]*input_mode' "$HELPER" 'frame input geometry ignores the explicit interactive mode'
require 'if\(input_mode\).*XShapeCombineRegion' "$HELPER" 'non-interactive frames can still install click-blocking input geometry'
! grep -q 'requested!=input_mode' "$HELPER" || fail 'heartbeat fallback overrides managed interactive mode every frame'
require 'client<0&&input_mode.*input_mode=0.*set_input' "$HELPER" 'client disconnect can retain a click-blocking input shape during reconnect grace'
require 'SO_RCVTIMEO' "$HELPER" 'accepted native socket has no explicit bounded receive mode'
require 'EAGAIN.*EWOULDBLOCK|EWOULDBLOCK.*EAGAIN' "$HELPER" 'native reads do not distinguish retryable errors from EOF'

require 'RandomNumberGenerator' "$CTO/NativeGpuProbe.cs" 'managed launch token is not cryptographically random'
require 'WaitForReady' "$CTO/NativeGpuProbe.cs" 'managed probe does not wait for authenticated readiness'
require 'IsConnected' "$CTO/NativeGpuProbe.cs" 'managed probe has no helper connection watchdog'
require 'SerializeShutdown' "$CTO/NativeGpuProbe.cs" 'managed stop does not request authenticated native shutdown'
require '"-1 0 0 0 0 0' "$CTO/NativeGpuProbe.cs" 'managed stop has no disconnected heartbeat fallback'
require 'SendTimeout' "$CTO/NativeGpuTransport.cs" 'managed sends have no bounded timeout'
require 'ReceiveTimeout' "$CTO/NativeGpuTransport.cs" 'managed receives have no bounded timeout'
require 'MouseInputMagic' "$CTO/NativeGpuTransport.cs" 'managed transport does not accept native mouse events'
require 'AddNativeMouseButton' "$CTO/NativeGpuProbe.cs" 'managed probe does not deliver native mouse buttons'
require 'uint32_t msg\[6\].*MOUSE_INPUT_MAGIC' "$HELPER" 'native mouse event framing is not length plus 20-byte payload'
require 'GAMEHELPER2_OVERLAY_BACKEND' "$CTO/Overlay.cs" 'native GPU backend selector is missing'
require '"native-gpu"' "$CTO/Overlay.cs" 'native GPU backend is not explicit opt-in'

cancel_line="$(grep -n 'cancellationTokenSource?.Cancel' "$CTO/Overlay.cs" | head -1 | cut -d: -f1)"
join_line="$(grep -n 'renderThread?.Join' "$CTO/Overlay.cs" | head -1 | cut -d: -f1)"
[[ -n "$cancel_line" && -n "$join_line" && "$cancel_line" -lt "$join_line" ]] || \
  fail 'Dispose does not cancel before joining the render thread'
require 'backBuffer\?\.Dispose' "$CTO/Overlay.cs" 'overlay disposal still calls fragile COM Release on the back buffer'
require 'renderView\?\.Dispose' "$CTO/Overlay.cs" 'overlay disposal still calls fragile COM Release on the render target'
require 'XGrabKey.*f12_keycode' "$HELPER" 'the hidden overlay has no passive F12 hotkey grab'
require 'NativeKeyState.Update' "$CTO/ImGuiInputHandler.cs" 'native key events do not update the managed hotkey state'
require 'NativeKeyState.ConsumePressed' "$CTO/Win32/Utils.cs" 'GameHelper hotkey polling does not consume native press edges'
require 'KEYBOARD_RETRY_SECONDS' "$HELPER" 'keyboard capture does not retain bounded retry state'
require 'close_client.*keyboard' "$HELPER" 'client close paths do not release keyboard capture'
require 'grab_error==BadAccess' "$HELPER" 'passive F12 BadAccess is not handled explicitly'
require 'if\(grab_error\).*XUngrabKey' "$HELPER" 'partial passive F12 grabs are not released after an X11 error'

printf 'PASS: native GPU protocol and lifecycle hardening guards are present\n'
