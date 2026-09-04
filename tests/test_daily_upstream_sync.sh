#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
workflow="$repo_root/.github/workflows/sync-upstream.yml"
sync_script="$repo_root/.github/scripts/sync-upstream.sh"

require() {
  local pattern=$1 file=$2 message=$3
  if ! grep -Eq -- "$pattern" "$file"; then
    printf 'FAIL: %s\n' "$message" >&2
    exit 1
  fi
}

[[ -f "$workflow" ]] || { echo 'FAIL: sync workflow missing' >&2; exit 1; }
[[ -x "$sync_script" ]] || { echo 'FAIL: sync script missing or not executable' >&2; exit 1; }
require "cron: ['\"]17 3 \\* \\* \\*['\"]" "$workflow" 'daily 03:17 UTC schedule missing'
require '^  contents: write$' "$workflow" 'workflow lacks repository-scoped write permission'
require 'sync-upstream\.sh' "$workflow" 'workflow does not invoke the tested sync script'
require 'uses: actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09' "$workflow" 'checkout action is not pinned to the verified v5 commit'
if grep -Eq '(pull_request_target|workflow_run|curl .*github\.com|GH_TOKEN|secrets\.)' "$workflow"; then
  echo 'FAIL: workflow has an unnecessary privileged or secret-bearing trigger/path' >&2
  exit 1
fi

sandbox=$(mktemp -d)
trap 'rm -rf "$sandbox"' EXIT
mkdir -p "$sandbox/empty-home"
export GIT_CONFIG_GLOBAL=/dev/null
export GIT_CONFIG_SYSTEM=/dev/null
export GIT_CONFIG_COUNT=1
export GIT_CONFIG_KEY_0=user.useConfigOnly
export GIT_CONFIG_VALUE_0=true
upstream_bare="$sandbox/upstream.git"
origin_bare="$sandbox/origin.git"
upstream_work="$sandbox/upstream-work"
fork_work="$sandbox/fork-work"
runner="$sandbox/runner"

git init --bare -q "$upstream_bare"
git init --bare -q "$origin_bare"
git init -q -b main "$upstream_work"
git -C "$upstream_work" config user.name test
git -C "$upstream_work" config user.email test@example.invalid
printf 'base\n' > "$upstream_work/shared.txt"
git -C "$upstream_work" add shared.txt
git -C "$upstream_work" commit -qm base
git -C "$upstream_work" remote add origin "$upstream_bare"
git -C "$upstream_work" push -q -u origin main
git --git-dir="$upstream_bare" symbolic-ref HEAD refs/heads/main

git clone -q "$upstream_bare" "$fork_work"
git -C "$fork_work" config user.name test
git -C "$fork_work" config user.email test@example.invalid
git -C "$fork_work" remote set-url origin "$origin_bare"
mkdir -p "$fork_work/.github/workflows"
printf 'fork workflow\n' > "$fork_work/.github/workflows/local.txt"
git -C "$fork_work" add .github/workflows/local.txt
git -C "$fork_work" commit -qm 'fork automation'
git -C "$fork_work" push -q -u origin main
git --git-dir="$origin_bare" symbolic-ref HEAD refs/heads/main

git clone -q "$origin_bare" "$runner"
# The production first run is commonly a no-op for code, but must seed the durable base.
(
  cd "$runner"
  UPSTREAM_URL="$upstream_bare" \
  UPSTREAM_BRANCH=main \
  TARGET_BRANCH=main \
  SYNC_BASE_BRANCH=upstream-sync-base \
  HOME="$sandbox/empty-home" \
    "$sync_script"
)
initial_upstream=$(git --git-dir="$upstream_bare" rev-parse refs/heads/main)
initial_base=$(git --git-dir="$origin_bare" rev-parse refs/heads/upstream-sync-base)
[[ "$initial_base" == "$initial_upstream" ]] || { echo 'FAIL: no-op sync did not seed durable base' >&2; exit 1; }

printf 'upstream change\n' > "$upstream_work/upstream.txt"
git -C "$upstream_work" add upstream.txt
git -C "$upstream_work" commit -qm 'upstream change'
git -C "$upstream_work" push -q

git -C "$runner" fetch -q origin main
git -C "$runner" reset -q --hard origin/main
(
  cd "$runner"
  UPSTREAM_URL="$upstream_bare" \
  UPSTREAM_BRANCH=main \
  TARGET_BRANCH=main \
  SYNC_BASE_BRANCH=upstream-sync-base \
  HOME="$sandbox/empty-home" \
    "$sync_script"
)

git -C "$runner" fetch -q origin main upstream-sync-base
upstream_head=$(git --git-dir="$upstream_bare" rev-parse refs/heads/main)
base_head=$(git --git-dir="$origin_bare" rev-parse refs/heads/upstream-sync-base)
[[ "$base_head" == "$upstream_head" ]] || { echo 'FAIL: durable upstream base not advanced' >&2; exit 1; }
git --git-dir="$origin_bare" show refs/heads/main:upstream.txt | grep -qx 'upstream change'
git --git-dir="$origin_bare" show refs/heads/main:.github/workflows/local.txt | grep -qx 'fork workflow'

# A conflicting upstream/fork edit must fail without changing origin/main.
git -C "$upstream_work" pull -q --ff-only
echo 'upstream conflict' > "$upstream_work/shared.txt"
git -C "$upstream_work" add shared.txt
git -C "$upstream_work" commit -qm 'upstream conflict'
git -C "$upstream_work" push -q

git -C "$runner" fetch -q origin main
git -C "$runner" reset -q --hard origin/main
echo 'fork conflict' > "$runner/shared.txt"
git -C "$runner" add shared.txt
git -C "$runner" -c user.name=test -c user.email=test@example.invalid commit -qm 'fork conflict'
git -C "$runner" push -q origin HEAD:main
before=$(git --git-dir="$origin_bare" rev-parse refs/heads/main)
if (
  cd "$runner"
  UPSTREAM_URL="$upstream_bare" \
  UPSTREAM_BRANCH=main \
  TARGET_BRANCH=main \
  SYNC_BASE_BRANCH=upstream-sync-base \
  HOME="$sandbox/empty-home" \
    "$sync_script"
); then
  echo 'FAIL: conflicting sync unexpectedly succeeded' >&2
  exit 1
fi
after=$(git --git-dir="$origin_bare" rev-parse refs/heads/main)
[[ "$before" == "$after" ]] || { echo 'FAIL: conflicting sync modified origin' >&2; exit 1; }

# A rewritten upstream history may advance only through the durable base delta.
git -C "$runner" merge --abort >/dev/null 2>&1 || true
git -C "$runner" reset -q --hard origin/main
git -C "$upstream_work" checkout -q --orphan rewritten-main
git -C "$upstream_work" rm -q -rf .
printf 'base\n' > "$upstream_work/shared.txt"
printf 'upstream change\n' > "$upstream_work/upstream.txt"
printf 'rewritten history\n' > "$upstream_work/rewrite.txt"
git -C "$upstream_work" add .
git -C "$upstream_work" commit -qm 'rewritten upstream'
git -C "$upstream_work" push -q --force origin HEAD:main
(
  cd "$runner"
  UPSTREAM_URL="$upstream_bare" \
  UPSTREAM_BRANCH=main \
  TARGET_BRANCH=main \
  SYNC_BASE_BRANCH=upstream-sync-base \
  HOME="$sandbox/empty-home" \
    "$sync_script"
)
git --git-dir="$origin_bare" show refs/heads/main:rewrite.txt | grep -qx 'rewritten history'
git --git-dir="$origin_bare" show refs/heads/main:shared.txt | grep -qx 'fork conflict'
rewritten_head=$(git --git-dir="$upstream_bare" rev-parse refs/heads/main)
rewritten_base=$(git --git-dir="$origin_bare" rev-parse refs/heads/upstream-sync-base)
[[ "$rewritten_base" == "$rewritten_head" ]] || { echo 'FAIL: rewritten upstream base not advanced' >&2; exit 1; }

printf 'PASS: daily upstream sync workflow is scheduled, bounded, and fail-closed\n'
