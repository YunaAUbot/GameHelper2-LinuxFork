#!/usr/bin/env bash
# Discover Steam, PoE2's library/prefix, and the Proton selected for PoE2.
# Explicit STEAM_ROOT, POE2_LIBRARY, and PROTON values take precedence.

POE2_APP_ID="${POE2_APP_ID:-2694490}"

gh2_env_fail() {
  echo "$1" >&2
  return 1
}

if [[ -z "${STEAM_ROOT:-}" ]]; then
  for candidate in "$HOME/.local/share/Steam" "$HOME/.steam/steam"; do
    if [[ -d "$candidate/steamapps" ]]; then
      STEAM_ROOT="$candidate"
      break
    fi
  done
fi
[[ -n "${STEAM_ROOT:-}" && -d "$STEAM_ROOT/steamapps" ]] || \
  gh2_env_fail "Steam installation not found. Set STEAM_ROOT to the directory containing steamapps/."

if [[ -z "${POE2_LIBRARY:-}" ]]; then
  gh2_libraries=("$STEAM_ROOT")
  for gh2_vdf in "$STEAM_ROOT/steamapps/libraryfolders.vdf" \
    "$HOME/.steam/steam/steamapps/libraryfolders.vdf"; do
    [[ -f "$gh2_vdf" ]] || continue
    while IFS= read -r gh2_line; do
      if [[ "$gh2_line" =~ \"path\"[[:space:]]*\"([^\"]+)\" ]]; then
        gh2_library="${BASH_REMATCH[1]}"
        gh2_library="${gh2_library//\\\\/\\}"
        gh2_libraries+=("$gh2_library")
      fi
    done < "$gh2_vdf"
  done

  for gh2_library in "${gh2_libraries[@]}"; do
    if [[ -d "$gh2_library/steamapps/compatdata/$POE2_APP_ID/pfx" ]]; then
      POE2_LIBRARY="$gh2_library"
      break
    fi
  done
fi
[[ -n "${POE2_LIBRARY:-}" && -d "$POE2_LIBRARY/steamapps" ]] || \
  gh2_env_fail "PoE2 Steam library not found. Set POE2_LIBRARY to the library directory."

if [[ -z "${PROTON:-}" ]]; then
  shopt -s nullglob
  gh2_proton_candidates=(
    "$STEAM_ROOT"/compatibilitytools.d/*/proton
    "$STEAM_ROOT"/steamapps/common/*/proton
    "$POE2_LIBRARY"/steamapps/common/*/proton
  )
  shopt -u nullglob

  gh2_config_info="$POE2_LIBRARY/steamapps/compatdata/$POE2_APP_ID/config_info"
  gh2_configured_tool=""
  gh2_config_text=""
  if [[ -f "$gh2_config_info" ]]; then
    IFS= read -r gh2_configured_tool < "$gh2_config_info" || true
    gh2_config_text="$(<"$gh2_config_info")"
  fi

  for gh2_candidate in "${gh2_proton_candidates[@]}"; do
    [[ -x "$gh2_candidate" ]] || continue
    gh2_candidate_dir="$(dirname -- "$gh2_candidate")"
    if [[ "$(basename -- "$gh2_candidate_dir")" == "$gh2_configured_tool" ]] ||
       [[ "$gh2_config_text" == *"$gh2_candidate_dir/"* ]]; then
      PROTON="$gh2_candidate"
      break
    fi
  done

  if [[ -z "${PROTON:-}" && ${#gh2_proton_candidates[@]} -gt 0 ]]; then
    mapfile -t gh2_sorted_candidates < <(printf '%s\n' "${gh2_proton_candidates[@]}" | sort -V)
    for ((gh2_i=${#gh2_sorted_candidates[@]} - 1; gh2_i >= 0; gh2_i--)); do
      if [[ -x "${gh2_sorted_candidates[$gh2_i]}" ]]; then
        PROTON="${gh2_sorted_candidates[$gh2_i]}"
        break
      fi
    done
  fi
fi
[[ -n "${PROTON:-}" && -x "$PROTON" ]] || \
  gh2_env_fail "Proton runner not found. Set PROTON to its executable."

export STEAM_ROOT POE2_LIBRARY PROTON POE2_APP_ID
