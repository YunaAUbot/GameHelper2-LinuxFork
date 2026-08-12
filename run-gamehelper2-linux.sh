#!/usr/bin/env bash
# Start GameHelper2 only after Path of Exile 2 was started manually through Steam.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
GH2_PROC_ROOT="${GH2_PROC_ROOT:-/proc}"
POE2_APP_ID="${POE2_APP_ID:-2694490}"
GH2_LOCK_FILE="${GH2_LOCK_FILE:-${XDG_RUNTIME_DIR:-/tmp}/gamehelper2-poe2-${UID}.lock}"

exec 9>"$GH2_LOCK_FILE"
if ! flock -n 9; then
  echo "GameHelper2 is already running for this desktop session." >&2
  exit 9
fi

normalize_process_name() {
  local value="$1"
  value="${value##*\\}"
  value="${value##*/}"
  printf '%s\n' "${value,,}"
}

process_name_matches_poe() {
  local pid_dir="$1" value="" name
  if [[ -r "$pid_dir/cmdline" ]]; then
    IFS= read -r -d '' value < "$pid_dir/cmdline" || true
    name="$(normalize_process_name "$value")"
    [[ "$name" == pathofexile* ]] && return 0
  fi
  if [[ -r "$pid_dir/comm" ]]; then
    IFS= read -r value < "$pid_dir/comm" || true
    name="$(normalize_process_name "$value")"
    [[ "$name" == pathofexile* ]] && return 0
  fi
  return 1
}

process_has_poe2_identity() {
  local pid_dir="$1" entry
  [[ -r "$pid_dir/environ" ]] || return 1
  while IFS= read -r -d '' entry; do
    case "$entry" in
      "SteamAppId=$POE2_APP_ID"|"SteamGameId=$POE2_APP_ID"|"STEAM_COMPAT_APP_ID=$POE2_APP_ID")
        return 0
        ;;
      STEAM_COMPAT_DATA_PATH=*/compatdata/"$POE2_APP_ID"|STEAM_COMPAT_DATA_PATH=*/compatdata/"$POE2_APP_ID"/*)
        return 0
        ;;
    esac
  done < "$pid_dir/environ"
  return 1
}

poe2_is_running() {
  local pid_dir
  for pid_dir in "$GH2_PROC_ROOT"/[0-9]*; do
    [[ -d "$pid_dir" ]] || continue
    if process_name_matches_poe "$pid_dir" && process_has_poe2_identity "$pid_dir"; then
      return 0
    fi
  done
  return 1
}

if ! poe2_is_running; then
  echo "Path of Exile 2 is not running. Start it through Steam first, then run this script." >&2
  exit 3
fi

if [[ -z "${GAMEHELPER2_EXE:-}" ]]; then
  for gh2_exe_candidate in \
    "$SCRIPT_DIR/GameHelper.exe" \
    "$SCRIPT_DIR/GameHelper/bin/Release/net10.0-windows/win-x64/GameHelper.exe"; do
    if [[ -f "$gh2_exe_candidate" ]]; then
      GAMEHELPER2_EXE="$gh2_exe_candidate"
      break
    fi
  done
  GAMEHELPER2_EXE="${GAMEHELPER2_EXE:-$SCRIPT_DIR/GameHelper.exe}"
fi
GH2_POLL_INTERVAL="${GH2_POLL_INTERVAL:-1}"

# shellcheck source=scripts/steam-proton-env.sh
source "$SCRIPT_DIR/scripts/steam-proton-env.sh"

[[ -f "$GAMEHELPER2_EXE" ]] || {
  echo "GameHelper2 executable not found: $GAMEHELPER2_EXE" >&2
  exit 4
}
[[ -n "${STEAM_ROOT:-}" && -d "$STEAM_ROOT/steamapps" ]] || {
  echo "Steam root is unavailable. Set STEAM_ROOT to the directory containing steamapps/." >&2
  exit 5
}
[[ -n "${POE2_LIBRARY:-}" && -d "$POE2_LIBRARY/steamapps" ]] || {
  echo "PoE2 Steam library is unavailable. Set POE2_LIBRARY to the library directory." >&2
  exit 6
}
[[ -n "${PROTON:-}" && -x "$PROTON" ]] || {
  echo "Proton runner is unavailable. Set PROTON to its executable." >&2
  exit 7
}

compat_data="$POE2_LIBRARY/steamapps/compatdata/$POE2_APP_ID"
[[ -d "$compat_data/pfx" ]] || {
  echo "PoE2 Proton prefix not found: $compat_data/pfx" >&2
  exit 8
}

helper_pid=""
helper_pgid=""
stop_helper() {
  [[ -n "$helper_pgid" ]] || return 0
  if kill -0 -- "-$helper_pgid" 2>/dev/null; then
    kill -TERM -- "-$helper_pgid" 2>/dev/null || true
    for _ in {1..10}; do
      ! kill -0 -- "-$helper_pgid" 2>/dev/null && break
      sleep 0.1
    done
    if kill -0 -- "-$helper_pgid" 2>/dev/null; then
      kill -KILL -- "-$helper_pgid" 2>/dev/null || true
    fi
  fi
  [[ -n "$helper_pid" ]] && wait "$helper_pid" 2>/dev/null || true
  helper_pid=""
  helper_pgid=""
}
trap 'stop_helper' EXIT
trap 'exit 130' INT TERM

GAMEHELPER2_OVERLAY_BACKEND="${GAMEHELPER2_OVERLAY_BACKEND:-native-gpu}"
PROTON_USE_WINED3D="${PROTON_USE_WINED3D:-1}"

echo "Path of Exile 2 detected; starting GameHelper2 only."
(
  cd -- "$(dirname -- "$GAMEHELPER2_EXE")"
  exec setsid env \
    STEAM_COMPAT_DATA_PATH="$compat_data" \
    STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT" \
    STEAM_COMPAT_APP_ID="$POE2_APP_ID" \
    GAMEHELPER2_OVERLAY_BACKEND="$GAMEHELPER2_OVERLAY_BACKEND" \
    PROTON_USE_WINED3D="$PROTON_USE_WINED3D" \
    "$PROTON" runinprefix "$GAMEHELPER2_EXE"
) &
helper_pid=$!
helper_pgid="$helper_pid"

echo "GameHelper2 started. It will stop automatically when Path of Exile 2 exits."
while kill -0 "$helper_pid" 2>/dev/null; do
  if ! poe2_is_running; then
    echo "Path of Exile 2 exited; stopping GameHelper2."
    stop_helper
    exit 0
  fi
  sleep "$GH2_POLL_INTERVAL"
done

set +e
wait "$helper_pid"
status=$?
set -e
stop_helper
exit "$status"
