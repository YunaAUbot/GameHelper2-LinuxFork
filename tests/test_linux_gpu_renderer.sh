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
grep -q 'GAMEHELPER2_NATIVE_FPS_LIMIT' "$CTO/Overlay.cs" || fail "native GPU loop has no launcher-controlled frame limit"
grep -q 'GAMEHELPER2_NATIVE_FPS_LIMIT' "$ROOT/run-gamehelper2-linux.sh" || fail "Linux launcher does not pass a native overlay frame limit"
python3 - "$CTO/Overlay.cs" "$CTO/ImGuiRenderer.cs" <<'PY'
import re
import sys
from pathlib import Path

overlay = Path(sys.argv[1]).read_text()
renderer = Path(sys.argv[2]).read_text()
method = overlay.split('private async Task InitializeResources()', 1)[1]
method = method.split('private bool ProcessMessage', 1)[0]
if not re.search(r'if\s*\(this\.useNativeGpu\).*?new ImGuiRenderer\([^;]*nativeOnly:\s*true', method, re.S):
    raise SystemExit('FAIL: native backend still requires the D3D11 initialization path')
if 'CaptureNativeFontAtlas' not in renderer or 'if (!this.nativeOnly)' not in renderer:
    raise SystemExit('FAIL: ImGui renderer cannot build a CPU-only native font atlas')
resize = overlay.split('private void OnResize', 1)[1].split('private async Task InitializeResources', 1)[0]
if not re.search(r'if\s*\(this\.useNativeGpu\).*?this\.renderer\.Resize\(width, height\).*?return;', resize, re.S):
    raise SystemExit('FAIL: native resize can still enter the D3D11 swapchain path')
PY
grep -q 'DriverType.Hardware' "$CTO/Overlay.cs" || fail "Windows backend no longer requests the hardware D3D11 device"
grep -q 'gamehelper2-gpu-overlay' "$CTO/NativeGpuProbe.cs" || fail "managed bridge does not resolve the packaged helper"
grep -q 'bounds.Height} 0 .*unixHeartbeat' "$CTO/NativeGpuProbe.cs" || fail "production native compositor still has a fixed wall-clock expiry"
grep -q 'InvalidateFont' "$CTO/NativeGpuProbe.cs" || fail "native font uploads cannot be invalidated after an atlas rebuild"
grep -q 'NativeGpuProbe.InvalidateFont' "$CTO/Overlay.cs" || fail "runtime font updates keep using a stale native atlas"
grep -q 'Path of Exile 2' "$HELPER" || fail "native compositor does not target the PoE2 window"
grep -q 'glXCreateNewContext' "$HELPER" || fail "native compositor is not GPU-backed GLX"
! grep -q 'glReadPixels' "$HELPER" || fail "native compositor performs a forbidden full-frame GPU readback"
grep -q 'GL_RENDERER' "$HELPER" || fail "native compositor does not record the active GL renderer"
grep -q '/tmp/gamehelper2-gpu-renderer.log' "$HELPER" || fail "GPU renderer diagnostic path is missing"

read -r -a helper_cflags <<< "$(pkg-config --cflags x11 xext xrender gl)"
cc -Wall -Wextra -Werror -O2 -fsyntax-only "$HELPER" "${helper_cflags[@]}"

printf 'PASS: local CTO uses a native GPU compositor without full-frame readback\n'
