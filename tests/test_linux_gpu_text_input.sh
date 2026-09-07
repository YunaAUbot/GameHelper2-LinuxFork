#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
RENDERER="${TEXT_RENDERER:-$ROOT/renderer-src/ClickableTransparentOverlay/ClickableTransparentOverlay/bin/Release/net8.0/win-x64/ClickableTransparentOverlay.dll}"
NATIVE="${TEXT_NATIVE:-}"
temporary_native=""
trap '[[ -z "$temporary_native" ]] || rm -f -- "$temporary_native"' EXIT
if [[ -z "$NATIVE" ]]; then
  temporary_native=$(mktemp /tmp/gh-text-native.XXXXXX)
  NATIVE="$temporary_native"
  read -r -a native_flags <<< "$(pkg-config --cflags --libs x11 xext xrender gl)"
  cc -Wall -Wextra -Werror -O2 "$ROOT/renderer-src/gamehelper2-gpu-overlay.c" "${native_flags[@]}" -lm -o "$NATIVE"
fi
"$DOTNET" build "$ROOT/tests/NativeTextInputProbe" -c Release -r linux-x64 -p:RendererPath="$RENDERER" --nologo -v:q
xvfb-run -a -s '-screen 0 1280x720x24 +extension GLX +extension XTEST +render -noreset' env GH_TEXT_FIXTURE=1 \
  "$DOTNET" "$ROOT/tests/NativeTextInputProbe/bin/Release/net10.0/linux-x64/NativeTextInputProbe.dll" "$NATIVE"
