#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
plugin="$repo_root/Plugins/DevBridge"

[[ -f "$plugin/DevBridge.csproj" ]]
[[ "$(rg -l 'sealed class .*: PCore<' "$plugin" -g '*.cs' | wc -l)" -eq 1 ]]
rg -q 'ProjectReference Include="\.\.\\\.\.\\GameHelper\\GameHelper.csproj"' "$plugin/DevBridge.csproj"
rg -q 'GameCurrentState != GameStateTypes.InGameState' "$plugin/DevBridgeCore.cs"
rg -q 'request.SchemaVersion != 1' "$plugin/DevBridgeCore.cs"
rg -Fq 'File.Move(requestPath, claimedPath, false)' "$plugin/DevBridgeCore.cs"
rg -Fq 'ReadBounded(claimedPath, MaxRequestBytes)' "$plugin/DevBridgeCore.cs"
rg -Fq 'ReadBounded(this.SettingsFile, MaxSettingsBytes)' "$plugin/DevBridgeCore.cs"
if rg -n 'WriteProcessMemory|SendInput|mouse_event|keybd_event|Process\.Start|HttpClient|Socket|TcpClient|UdpClient|Assembly\.Load|CSharpScript|Address|IntPtr' "$plugin" -g '*.cs'; then
  exit 1
fi
rg -Fq '# Passive packaging manifest: Plugins\\DevBridge\\DevBridge.csproj' "$repo_root/GameOverlay.Linux.sln"
rg -q 'DevBridge' "$repo_root/build-linux-package.sh"
