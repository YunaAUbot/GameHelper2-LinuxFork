#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
LAUNCHER="$REPO_ROOT/run-gamehelper2-linux.sh"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

make_fixture() {
  FIXTURE="$(mktemp -d)"
  mkdir -p "$FIXTURE/proc" "$FIXTURE/steam/steamapps" \
    "$FIXTURE/library/steamapps/compatdata/2694490/pfx"
  cat > "$FIXTURE/proton" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$PROTON_CALLS"
printf '%s\n' "$$" > "$PROTON_PID_FILE"
if [[ -n "${PROTON_PWD_FILE:-}" ]]; then
  pwd > "$PROTON_PWD_FILE"
fi
if [[ "${PROTON_SPAWN_TERM_RESISTANT_CHILD:-0}" == "1" ]]; then
  bash -c 'trap "" TERM INT; while :; do sleep 1; done' &
  printf '%s\n' "$!" > "${PROTON_CHILD_PID_FILE:-$PROTON_PID_FILE-child}"
fi
trap 'exit 0' TERM INT
while :; do sleep 1; done
EOF
  chmod +x "$FIXTURE/proton"
  : > "$FIXTURE/GameHelper.exe"
  : > "$FIXTURE/proton-calls"
}

cleanup() {
  if [[ -n "${LAUNCHER_PID:-}" ]] && kill -0 "$LAUNCHER_PID" 2>/dev/null; then
    kill "$LAUNCHER_PID" 2>/dev/null || true
    wait "$LAUNCHER_PID" 2>/dev/null || true
  fi
  if [[ -n "${FIXTURE:-}" && -s "$FIXTURE/proton-pid" ]]; then
    proton_group="$(cat "$FIXTURE/proton-pid")"
    kill -KILL -- "-$proton_group" 2>/dev/null || true
  fi
  [[ -n "${FIXTURE:-}" ]] && rm -rf "$FIXTURE"
}
trap cleanup EXIT

# A manually started PoE2 process is required; the launcher must not start the
# helper or silently create a lingering Proton context when the game is absent.
make_fixture
set +e
output="$({
  GH2_PROC_ROOT="$FIXTURE/proc" \
  GAMEHELPER2_EXE="$FIXTURE/GameHelper.exe" \
  STEAM_ROOT="$FIXTURE/steam" \
  POE2_LIBRARY="$FIXTURE/library" \
  PROTON="$FIXTURE/proton" \
  PROTON_CALLS="$FIXTURE/proton-calls" \
    "$LAUNCHER"
} 2>&1)"
status=$?
set -e

[[ $status -ne 0 ]] || fail "launcher succeeded while PoE2 was absent"
[[ "$output" == *"Path of Exile 2 is not running"* ]] || \
  fail "missing clear game-not-running message: $output"
[[ ! -s "$FIXTURE/proton-calls" ]] || fail "Proton was invoked while PoE2 was absent"

printf 'PASS: refuses to launch helper while PoE2 is absent\n'

# PoE1 uses the same Windows executable names. Require PoE2's Steam app
# identity as well, otherwise a running PoE1 instance would be a false match.
rm -rf "$FIXTURE"
FIXTURE=""
make_fixture
mkdir -p "$FIXTURE/proc/3131"
printf 'Z:\\games\\PathOfExileSteam.exe\0' > "$FIXTURE/proc/3131/cmdline"
printf 'SteamAppId=238960\0' > "$FIXTURE/proc/3131/environ"
set +e
output="$({
  GH2_PROC_ROOT="$FIXTURE/proc" \
  GAMEHELPER2_EXE="$FIXTURE/GameHelper.exe" \
  STEAM_ROOT="$FIXTURE/steam" \
  POE2_LIBRARY="$FIXTURE/library" \
  PROTON="$FIXTURE/proton" \
  PROTON_CALLS="$FIXTURE/proton-calls" \
  PROTON_PID_FILE="$FIXTURE/proton-pid" \
    timeout --signal=TERM 1 "$LAUNCHER"
} 2>&1)"
status=$?
set -e
[[ $status -ne 0 ]] || fail "launcher accepted PoE1 as PoE2"
[[ "$output" == *"Path of Exile 2 is not running"* ]] || \
  fail "PoE1 rejection did not report that PoE2 is absent: $output"
[[ ! -s "$FIXTURE/proton-calls" ]] || fail "helper was launched for PoE1 instead of PoE2"

printf 'PASS: does not confuse PoE1 with PoE2\n'

# Wine may expose a generic preloader as argv[0] and truncate the Windows name
# in /proc/PID/comm. The Steam app identity still makes this an unambiguous PoE2
# process.
rm -rf "$FIXTURE"
FIXTURE=""
make_fixture
mkdir -p "$FIXTURE/proc/3232"
printf '/usr/lib/wine/wine64-preloader\0' > "$FIXTURE/proc/3232/cmdline"
printf 'PathOfExileSte\n' > "$FIXTURE/proc/3232/comm"
printf 'SteamAppId=2694490\0' > "$FIXTURE/proc/3232/environ"
GH2_PROC_ROOT="$FIXTURE/proc" \
GH2_POLL_INTERVAL=0.05 \
GAMEHELPER2_EXE="$FIXTURE/GameHelper.exe" \
STEAM_ROOT="$FIXTURE/steam" \
POE2_LIBRARY="$FIXTURE/library" \
PROTON="$FIXTURE/proton" \
PROTON_CALLS="$FIXTURE/proton-calls" \
PROTON_PID_FILE="$FIXTURE/proton-pid" \
  "$LAUNCHER" >"$FIXTURE/launcher-output" 2>&1 &
LAUNCHER_PID=$!
for _ in {1..100}; do
  [[ -s "$FIXTURE/proton-calls" ]] && break
  kill -0 "$LAUNCHER_PID" 2>/dev/null || break
  sleep 0.02
done
[[ -s "$FIXTURE/proton-calls" ]] || \
  fail "Wine preloader/truncated comm PoE2 process was not detected: $(cat "$FIXTURE/launcher-output")"
rm -rf "$FIXTURE/proc/3232"
for _ in {1..100}; do
  ! kill -0 "$LAUNCHER_PID" 2>/dev/null && break
  sleep 0.02
done
set +e
wait "$LAUNCHER_PID"
status=$?
set -e
LAUNCHER_PID=""
[[ $status -eq 0 ]] || fail "Wine-process launch returned $status"

printf 'PASS: recognizes PoE2 behind Wine process naming\n'

# If PoE2 was started manually, launch only GameHelper2 in its Proton context.
# When the game process disappears, terminate the helper and return cleanly so
# Steam's compatibility context cannot be kept alive by the overlay.
rm -rf "$FIXTURE"
FIXTURE=""
make_fixture
mkdir -p "$FIXTURE/runtime"
mv "$FIXTURE/GameHelper.exe" "$FIXTURE/runtime/GameHelper.exe"
mkdir -p "$FIXTURE/proc/4242"
printf 'Z:\\games\\PathOfExileSteam.exe\0' > "$FIXTURE/proc/4242/cmdline"
printf 'SteamAppId=2694490\0' > "$FIXTURE/proc/4242/environ"

GH2_PROC_ROOT="$FIXTURE/proc" \
GH2_POLL_INTERVAL=0.05 \
GAMEHELPER2_EXE="$FIXTURE/runtime/GameHelper.exe" \
STEAM_ROOT="$FIXTURE/steam" \
POE2_LIBRARY="$FIXTURE/library" \
PROTON="$FIXTURE/proton" \
PROTON_CALLS="$FIXTURE/proton-calls" \
PROTON_PID_FILE="$FIXTURE/proton-pid" \
PROTON_PWD_FILE="$FIXTURE/proton-pwd" \
PROTON_SPAWN_TERM_RESISTANT_CHILD=1 \
  "$LAUNCHER" >"$FIXTURE/launcher-output" 2>&1 &
LAUNCHER_PID=$!

for _ in {1..100}; do
  [[ -s "$FIXTURE/proton-calls" ]] && break
  kill -0 "$LAUNCHER_PID" 2>/dev/null || break
  sleep 0.02
done
[[ -s "$FIXTURE/proton-calls" ]] || \
  fail "helper was not launched for a running manually-started PoE2: $(cat "$FIXTURE/launcher-output")"
[[ "$(cat "$FIXTURE/proton-calls")" == "run $FIXTURE/runtime/GameHelper.exe" ]] || \
  fail "unexpected Proton invocation: $(cat "$FIXTURE/proton-calls")"
[[ "$(cat "$FIXTURE/proton-pwd")" == "$FIXTURE/runtime" ]] || \
  fail "helper was not started from its runtime directory: $(cat "$FIXTURE/proton-pwd")"

rm -rf "$FIXTURE/proc/4242"
for _ in {1..100}; do
  ! kill -0 "$LAUNCHER_PID" 2>/dev/null && break
  sleep 0.02
done
if kill -0 "$LAUNCHER_PID" 2>/dev/null; then
  fail "launcher stayed alive after PoE2 exited"
fi
set +e
wait "$LAUNCHER_PID"
status=$?
set -e
LAUNCHER_PID=""
[[ $status -eq 0 ]] || fail "launcher returned $status after normal PoE2 exit"

proton_pid="$(cat "$FIXTURE/proton-pid")"
proton_child_pid="$(cat "$FIXTURE/proton-pid-child")"
for _ in {1..100}; do
  ! kill -0 "$proton_pid" 2>/dev/null && break
  sleep 0.02
done
! kill -0 "$proton_pid" 2>/dev/null || fail "helper Proton process survived PoE2 exit"
for _ in {1..100}; do
  ! kill -0 "$proton_child_pid" 2>/dev/null && break
  sleep 0.02
done
! kill -0 "$proton_child_pid" 2>/dev/null || \
  fail "TERM-resistant helper descendant survived PoE2 exit"

printf 'PASS: follows a manually-started PoE2 and stops helper on game exit\n'

# With no path overrides, discover a secondary Steam library and the exact
# Proton installation referenced by PoE2's config_info.
rm -rf "$FIXTURE"
FIXTURE=""
make_fixture
home="$FIXTURE/home"
steam="$home/.local/share/Steam"
library="$FIXTURE/Secondary Library"
proton_dir="$steam/steamapps/common/Proton - Experimental"
mkdir -p "$steam/steamapps" "$library/steamapps/compatdata/2694490/pfx" \
  "$proton_dir/files/share/fonts" "$FIXTURE/proc/5151"
cp "$FIXTURE/proton" "$proton_dir/proton"
printf 'Z:\\games\\PathOfExileSteam.exe\0' > "$FIXTURE/proc/5151/cmdline"
printf 'STEAM_COMPAT_APP_ID=2694490\0' > "$FIXTURE/proc/5151/environ"
printf '11.0-100\n%s/files/share/fonts/\n' "$proton_dir" > \
  "$library/steamapps/compatdata/2694490/config_info"
cat > "$steam/steamapps/libraryfolders.vdf" <<EOF
"libraryfolders"
{
  "0" { "path" "$steam" }
  "1" { "path" "$library" }
}
EOF

HOME="$home" \
GH2_PROC_ROOT="$FIXTURE/proc" \
GH2_POLL_INTERVAL=0.05 \
GAMEHELPER2_EXE="$FIXTURE/GameHelper.exe" \
PROTON_CALLS="$FIXTURE/proton-calls" \
PROTON_PID_FILE="$FIXTURE/proton-pid" \
  env -u STEAM_ROOT -u POE2_LIBRARY -u PROTON "$LAUNCHER" \
  >"$FIXTURE/launcher-output" 2>&1 &
LAUNCHER_PID=$!
for _ in {1..100}; do
  [[ -s "$FIXTURE/proton-calls" ]] && break
  kill -0 "$LAUNCHER_PID" 2>/dev/null || break
  sleep 0.02
done
[[ -s "$FIXTURE/proton-calls" ]] || \
  fail "automatic Steam/Proton discovery failed: $(cat "$FIXTURE/launcher-output")"
rm -rf "$FIXTURE/proc/5151"
for _ in {1..100}; do
  ! kill -0 "$LAUNCHER_PID" 2>/dev/null && break
  sleep 0.02
done
set +e
wait "$LAUNCHER_PID"
status=$?
set -e
LAUNCHER_PID=""
[[ $status -eq 0 ]] || fail "auto-discovered launch returned $status"

printf 'PASS: discovers PoE2 library and configured Proton automatically\n'

# A developer checkout should use the normal Release output without requiring
# GAMEHELPER2_EXE, while packaged releases still prefer GameHelper.exe beside
# the launcher.
rm -rf "$FIXTURE"
FIXTURE=""
make_fixture
checkout="$FIXTURE/checkout"
mkdir -p "$checkout/scripts" \
  "$checkout/GameHelper/bin/Release/net10.0-windows/win-x64" \
  "$FIXTURE/proc/6262"
cp "$LAUNCHER" "$checkout/run-gamehelper2-linux.sh"
cp "$REPO_ROOT/scripts/steam-proton-env.sh" "$checkout/scripts/steam-proton-env.sh"
build_exe="$checkout/GameHelper/bin/Release/net10.0-windows/win-x64/GameHelper.exe"
: > "$build_exe"
printf 'Z:\\games\\PathOfExileSteam.exe\0' > "$FIXTURE/proc/6262/cmdline"
printf 'STEAM_COMPAT_DATA_PATH=/tmp/compatdata/2694490\0' > "$FIXTURE/proc/6262/environ"

GH2_PROC_ROOT="$FIXTURE/proc" \
GH2_POLL_INTERVAL=0.05 \
STEAM_ROOT="$FIXTURE/steam" \
POE2_LIBRARY="$FIXTURE/library" \
PROTON="$FIXTURE/proton" \
PROTON_CALLS="$FIXTURE/proton-calls" \
PROTON_PID_FILE="$FIXTURE/proton-pid" \
  env -u GAMEHELPER2_EXE "$checkout/run-gamehelper2-linux.sh" \
  >"$FIXTURE/launcher-output" 2>&1 &
LAUNCHER_PID=$!
for _ in {1..100}; do
  [[ -s "$FIXTURE/proton-calls" ]] && break
  kill -0 "$LAUNCHER_PID" 2>/dev/null || break
  sleep 0.02
done
[[ "$(cat "$FIXTURE/proton-calls")" == "run $build_exe" ]] || \
  fail "developer Release output was not selected: $(cat "$FIXTURE/launcher-output")"
rm -rf "$FIXTURE/proc/6262"
for _ in {1..100}; do
  ! kill -0 "$LAUNCHER_PID" 2>/dev/null && break
  sleep 0.02
done
set +e
wait "$LAUNCHER_PID"
status=$?
set -e
LAUNCHER_PID=""
[[ $status -eq 0 ]] || fail "developer-checkout launch returned $status"

printf 'PASS: selects developer Release output automatically\n'
