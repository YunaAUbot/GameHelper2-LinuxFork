#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output="${1:-$repo_root/dist/GameHelper2-linux}"
dotnet=/root/.dotnet/dotnet
configuration=Release
framework=net10.0-windows
runtime=win-x64
build_output="$repo_root/GameHelper/bin/$configuration/$framework/$runtime"
passive_plugins=(PreloadAlert Radar HealthBars Atlas2 PlayerBuffBar LootValue NinjaPricer)

[[ $# -le 1 ]] || { printf 'usage: %s [output-directory]\n' "$0" >&2; exit 2; }
[[ "$output" = /* ]] || output="$PWD/$output"
output="$(realpath -m -- "$output")"
if [[ "$output" == / || "$output" == "$repo_root" || ( "$output" == "$repo_root/"* && "$output" != "$repo_root/dist/"* ) ]]; then
  printf 'refusing unsafe output directory: %s\n' "$output" >&2
  exit 2
fi
output_parent="$(dirname -- "$output")"
mkdir -p "$output_parent"
stage="$(mktemp -d "$output_parent/.GameHelper2-linux.XXXXXX")"
publish="$stage/publish"
native_helper="$stage/gamehelper2-gpu-overlay"
previous_dir=""
replacement_installed=0
cleanup() {
  status=$?
  trap - EXIT
  if [[ "$replacement_installed" -eq 0 && -n "$previous_dir" && ! -e "$output" && -e "$previous_dir/output" ]]; then
    mv -- "$previous_dir/output" "$output"
  fi
  rm -rf -- "$stage"
  [[ -z "$previous_dir" ]] || rm -rf -- "$previous_dir"
  exit "$status"
}
trap cleanup EXIT

"$dotnet" build "$repo_root/GameOverlay.Linux.sln" \
  -c "$configuration" -p:GenerateDocumentationFile=false \
  -p:EnableWindowsTargeting=true \
  -p:SkipNativeGpuOverlayBuild=true
"$dotnet" publish "$repo_root/GameHelper/GameHelper.csproj" \
  -c "$configuration" -f "$framework" -r "$runtime" \
  --self-contained true -p:GenerateDocumentationFile=false \
  -p:SkipNativeGpuOverlayBuild=true -o "$publish"

read -r -a native_cflags <<< "$(pkg-config --cflags x11 xext xrender gl)"
read -r -a native_libs <<< "$(pkg-config --libs x11 xext xrender gl)"
cc -Wall -Wextra -Werror -Wno-unused-result -O2 \
  "$repo_root/renderer-src/gamehelper2-gpu-overlay.c" \
  "${native_cflags[@]}" "${native_libs[@]}" -lm -o "$native_helper"
install -m 0755 "$native_helper" "$publish/gamehelper2-gpu-overlay"

mkdir -p "$publish/Plugins" "$publish/licenses"
cp "$repo_root/README-LINUX.md" "$publish/README-LINUX.md"
for plugin in "${passive_plugins[@]}"; do
  source_dir="$build_output/Plugins/$plugin"
  [[ -f "$source_dir/$plugin.dll" ]] || {
    printf 'missing built plugin: %s\n' "$source_dir/$plugin.dll" >&2
    exit 1
  }
  mkdir -p "$publish/Plugins/$plugin"
  cp -a "$source_dir/." "$publish/Plugins/$plugin/"
  rm -rf -- "$publish/Plugins/$plugin/config" "$publish/Plugins/$plugin/configs"
  rm -f -- "$publish/Plugins/$plugin/price_cache.json" \
    "$publish/Plugins/$plugin"/price_cache.json.tmp-*
done

cp "$repo_root/renderer-src/ClickableTransparentOverlay/LICENSE" \
  "$publish/licenses/ClickableTransparentOverlay.LICENSE"
if [[ -f "$repo_root/LICENSE" ]]; then
  cp "$repo_root/LICENSE" "$publish/licenses/GameHelper2.LICENSE"
else
  cp "$repo_root/README.md" "$publish/licenses/GameHelper2-PROVENANCE.md"
fi

# Reject runtime state or excluded plugin content even if an upstream build
# target starts producing it in the future.
for forbidden in configs logs credentials AutoHotKeyTrigger PickupHelper; do
  if find "$publish" \( -type d -o -type f \) -iname "$forbidden" -print -quit | grep -q .; then
    printf 'refusing to package forbidden path: %s\n' "$forbidden" >&2
    exit 1
  fi
done
if find "$publish" -type f \( -iname '*.log' -o -iname '.env' -o -iname 'credentials*' -o -iname 'secrets*' -o -iname 'price_cache.json' -o -iname 'price_cache.json.tmp-*' \) -print -quit | grep -q .; then
  printf 'refusing to package runtime log, cache, or credential material\n' >&2
  exit 1
fi

replacement="$stage/final"
mv -- "$publish" "$replacement"
if [[ -e "$output" || -L "$output" ]]; then
  previous_dir="$(mktemp -d "$output_parent/.GameHelper2-previous.XXXXXX")"
  mv -- "$output" "$previous_dir/output"
fi
if [[ "${GH2_PACKAGE_TEST_ABORT_AFTER_BACKUP:-0}" == 1 ]]; then
  exit 97
fi
mv -- "$replacement" "$output"
replacement_installed=1
if [[ -n "$previous_dir" ]]; then
  rm -rf -- "$previous_dir"
  previous_dir=""
fi
printf 'Linux package created at %s\n' "$output"
