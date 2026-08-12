#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
CTO="$ROOT/renderer-src/ClickableTransparentOverlay/ClickableTransparentOverlay"
HELPER="$ROOT/renderer-src/gamehelper2-gpu-overlay.c"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
require() { grep -Eq "$1" "$2" || fail "$3"; }

require 'NativeTextureMagic' "$CTO/NativeGpuFrameProtocol.cs" 'managed protocol has no bounded native texture upload message'
require 'SerializeTexture' "$CTO/NativeGpuFrameProtocol.cs" 'managed protocol cannot serialize texture pixels'
require 'MaxNativeTextureChunkBytes' "$CTO/NativeGpuFrameProtocol.cs" 'managed texture upload has no per-frame chunk bound'
require 'NativeTexture' "$CTO/ImGuiRenderer.cs" 'native renderer does not retain CPU texture data'
require 'GetPendingNativeTextures' "$CTO/ImGuiRenderer.cs" 'native renderer cannot enumerate one-shot pending texture uploads'
require 'AcknowledgeNativeTexture' "$CTO/ImGuiRenderer.cs" 'native renderer cannot acknowledge successful uploads'
require 'GetPendingNativeTextureDeletes' "$CTO/ImGuiRenderer.cs" 'native renderer cannot propagate texture removal'
require 'SerializeTextureDelete' "$CTO/NativeGpuFrameProtocol.cs" 'managed protocol cannot serialize texture deletion'
require 'NativeTextureAckMagic' "$CTO/NativeGpuFrameProtocol.cs" 'managed protocol cannot receive native texture acknowledgements'
require 'SerializeTexture\(' "$CTO/NativeGpuProbe.cs" 'native probe does not upload pending textures'
require 'TEXTURE_MAGIC' "$HELPER" 'GLX parser has no texture upload message'
require 'MAX_TEXTURE' "$HELPER" 'GLX texture uploads have no explicit bounds'
require 'glTexImage2D' "$HELPER" 'GLX does not upload transferred textures'
require 'native_texture_upload' "$HELPER" 'GLX cannot assemble bounded texture chunks'
require 'static int send_texture_ack' "$HELPER" 'texture acknowledgement failures cannot close the client'
require 'reset_texture_upload\(&texture_upload\)' "$HELPER" 'partial texture assembly is not reset at connection boundaries'
require 'texture_id' "$HELPER" 'frame commands do not reference transferred texture IDs'

printf 'PASS: bounded native texture upload lifecycle is implemented\n'
