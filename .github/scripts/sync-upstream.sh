#!/usr/bin/env bash
set -euo pipefail

upstream_url=${UPSTREAM_URL:-https://github.com/Gordin/GameHelper2.git}
upstream_branch=${UPSTREAM_BRANCH:-main}
target_branch=${TARGET_BRANCH:-main}
sync_base_branch=${SYNC_BASE_BRANCH:-upstream-sync-base}

if git remote get-url upstream >/dev/null 2>&1; then
  git remote set-url upstream "$upstream_url"
else
  git remote add upstream "$upstream_url"
fi

git fetch --no-tags upstream "$upstream_branch"
git fetch --no-tags origin "$target_branch"
git checkout -B "$target_branch" "origin/$target_branch"
git config user.name 'github-actions[bot]'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'

upstream_ref="upstream/$upstream_branch"
upstream_head=$(git rev-parse "$upstream_ref")
base_ref="refs/remotes/origin/$sync_base_branch"
if git fetch --no-tags origin "$sync_base_branch:$base_ref" 2>/dev/null; then
  upstream_base=$(git rev-parse "$base_ref")
else
  upstream_base=''
fi

if git merge-base --is-ancestor "$upstream_ref" HEAD; then
  echo 'Fork already contains the current upstream head.'
else
  if git merge-base HEAD "$upstream_ref" >/dev/null; then
    git merge --no-ff --no-edit "$upstream_ref"
  else
    if [[ -z "$upstream_base" ]]; then
      echo '::error::Upstream history was rewritten before a durable sync base existed; no files were pushed.'
      exit 1
    fi

    echo 'Upstream has no common history; attempting a three-way content merge.'
    patch_file=$(mktemp)
    trap 'rm -f "$patch_file"' EXIT
    git diff --binary "$upstream_base" "$upstream_head" -- . > "$patch_file"
    if [[ -s "$patch_file" ]] && ! git apply --3way --index "$patch_file"; then
      echo '::error::Rewritten upstream overlaps fork changes; no files were pushed.'
      exit 1
    fi
    if ! git diff --cached --quiet; then
      git commit -m 'Sync rewritten upstream snapshot'
    fi
    git merge --allow-unrelated-histories -s ours --no-edit "$upstream_ref"
  fi
fi

# Update code and the durable upstream snapshot together. The metadata branch
# deliberately tracks upstream exactly, including a future history rewrite.
git push --atomic origin \
  "HEAD:refs/heads/$target_branch" \
  "+$upstream_head:refs/heads/$sync_base_branch"
