#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
CTO="$ROOT/renderer-src/ClickableTransparentOverlay/ClickableTransparentOverlay"
HELPER="$ROOT/renderer-src/gamehelper2-gpu-overlay.c"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

[[ -f "$CTO/ClickableTransparentOverlay.csproj" ]] || fail "local CTO project is missing"
[[ -f "$CTO/NativeGpuFrameProtocol.cs" ]] || fail "managed GPU frame protocol is missing"
[[ -f "$CTO/NativeGpuTransport.cs" ]] || fail "managed GPU transport is missing"
[[ -f "$CTO/NativeGpuProbe.cs" ]] || fail "native GPU compositor lifecycle bridge is missing"
[[ -f "$HELPER" ]] || fail "native X11/GLX compositor source is missing"

grep -q '<ProjectReference Include="..\\renderer-src\\ClickableTransparentOverlay\\ClickableTransparentOverlay\\ClickableTransparentOverlay.csproj"' \
  "$ROOT/GameHelper/GameHelper.csproj" || fail "GameHelper does not reference the local CTO project"
! grep -q 'PackageReference Include="ClickableTransparentOverlay"' "$ROOT/GameHelper/GameHelper.csproj" || \
  fail "GameHelper still references the upstream CTO NuGet package"

grep -q 'GAMEHELPER2_OVERLAY_BACKEND' "$CTO/Overlay.cs" || fail "GameHelper2 backend selector is missing"
grep -q 'GameHelper2.renderer.log' "$CTO/Overlay.cs" || fail "managed renderer diagnostics are missing"
grep -q 'native GPU helper unavailable' "$CTO/Overlay.cs" || fail "native GPU startup does not fail closed"
grep -q 'NativeGpu' "$CTO/Overlay.cs" || fail "GPU compositor is not integrated into Overlay"
grep -q 'DriverType.Hardware' "$CTO/Overlay.cs" || fail "overlay no longer requests the hardware D3D11 device"
grep -q 'gamehelper2-gpu-overlay' "$CTO/NativeGpuProbe.cs" || fail "managed bridge does not resolve the packaged helper"
grep -q 'Path of Exile 2' "$HELPER" || fail "native compositor does not target the PoE2 window"
grep -q 'glXCreateNewContext' "$HELPER" || fail "native compositor is not GPU-backed GLX"
! grep -q 'glReadPixels' "$HELPER" || fail "native compositor performs a forbidden full-frame GPU readback"
grep -q 'GL_RENDERER' "$HELPER" || fail "native compositor does not record the active GL renderer"
grep -q '/tmp/gamehelper2-gpu-renderer.log' "$HELPER" || fail "GPU renderer diagnostic path is missing"

read -r -a helper_cflags <<< "$(pkg-config --cflags x11 xext xrender gl)"
cc -Wall -Wextra -Werror -O2 -fsyntax-only "$HELPER" "${helper_cflags[@]}"

printf 'PASS: local CTO uses a native GPU compositor without full-frame readback\n'
