#!/usr/bin/env bash
# Synthetic public text only; every input event targets this script's private outer Xvfb.
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" == --session ]]; then
    export GH_TEXT_FIXTURE=1
    "$WAYLAND_FIXTURE_BUILD/wayland-standin" > "$GH_FIXTURE_WAYLAND_LOG" 2>&1 &
    standin=$!
    trap 'kill "$standin" 2>/dev/null || true' EXIT
    sleep 2
    combined="$XDG_RUNTIME_DIR/fixture-xauthority"
    touch "$combined"
    xauth -f "$combined" merge "$XAUTHORITY" "$GH_OUTER_XAUTHORITY"
    export XAUTHORITY="$combined" GH_OUTER_XAUTHORITY="$combined"
    "$DOTNET" "$ROOT/tests/NativeWaylandTextInputProbe/bin/Release/net10.0/linux-x64/NativeWaylandTextInputProbe.dll" "$TEXT_NATIVE" > "$WAYLAND_FIXTURE_RESULTS/probe.log" 2>&1
    exit
fi
if [[ "${1:-}" == --kwin ]]; then
    runtime=$(mktemp -d /tmp/gh-kwin-XXXXXX)
    trap 'rm -rf "$runtime"' EXIT
    export XDG_RUNTIME_DIR="$runtime" GH_OUTER_DISPLAY="$DISPLAY" GH_OUTER_XAUTHORITY="$XAUTHORITY"
    export QT_QPA_PLATFORM=xcb KWIN_COMPOSE=Q LIBGL_ALWAYS_SOFTWARE=1 XDG_CONFIG_HOME="$runtime/config"
    mkdir -p "$XDG_CONFIG_HOME"
    timeout 80s dbus-run-session -- kwin_wayland --x11-display "$DISPLAY" --width 800 --height 600 --xwayland --no-lockscreen --no-global-shortcuts --no-kactivities --exit-with-session "$ROOT/tests/test_linux_wayland_text_input.sh --session" > "$WAYLAND_FIXTURE_RESULTS/kwin.log" 2>&1
    exit
fi
export DOTNET="${DOTNET:-dotnet}"
export TEXT_NATIVE="${TEXT_NATIVE:-}"
TEXT_RENDERER="${TEXT_RENDERER:-$ROOT/renderer-src/ClickableTransparentOverlay/ClickableTransparentOverlay/bin/Release/net8.0/win-x64/ClickableTransparentOverlay.dll}"
export WAYLAND_FIXTURE_RESULTS="${WAYLAND_FIXTURE_RESULTS:-$ROOT/artifacts/text-input/wayland-regression}"
mkdir -p "$WAYLAND_FIXTURE_RESULTS"
export WAYLAND_FIXTURE_BUILD
WAYLAND_FIXTURE_BUILD=$(mktemp -d /tmp/gh-wayland-build-XXXXXX)
trap 'rm -rf "$WAYLAND_FIXTURE_BUILD"' EXIT
if [[ -z "$TEXT_NATIVE" ]]; then
    TEXT_NATIVE="$WAYLAND_FIXTURE_BUILD/gamehelper2-gpu-overlay"
    read -r -a native_flags <<< "$(pkg-config --cflags --libs x11 xext xrender gl)"
    cc -Wall -Wextra -Werror -O2 "$ROOT/renderer-src/gamehelper2-gpu-overlay.c" "${native_flags[@]}" -lm -o "$TEXT_NATIVE"
fi
export GH_FIXTURE_WAYLAND_LOG="$WAYLAND_FIXTURE_RESULTS/wayland.log"
export GH_WAYLAND_GAME=1 GH_LIFECYCLE="${GH_LIFECYCLE:-f12}"
wayland-scanner client-header /usr/share/wayland-protocols/stable/xdg-shell/xdg-shell.xml "$WAYLAND_FIXTURE_BUILD/xdg-shell-client-protocol.h"
wayland-scanner private-code /usr/share/wayland-protocols/stable/xdg-shell/xdg-shell.xml "$WAYLAND_FIXTURE_BUILD/xdg-shell-protocol.c"
gcc -Wall -Wextra -I"$WAYLAND_FIXTURE_BUILD" -o "$WAYLAND_FIXTURE_BUILD/wayland-standin" "$ROOT/tests/NativeWaylandTextInputProbe/wayland-standin.c" "$WAYLAND_FIXTURE_BUILD/xdg-shell-protocol.c" -lwayland-client
"$DOTNET" build "$ROOT/tests/NativeWaylandTextInputProbe" -c Release -r linux-x64 -p:RendererPath="$TEXT_RENDERER" --nologo -v:q
xvfb-run -a -s '-screen 0 800x600x24 +extension GLX +extension XTEST +render -noreset' "$ROOT/tests/test_linux_wayland_text_input.sh" --kwin
cat "$WAYLAND_FIXTURE_RESULTS/probe.log"
