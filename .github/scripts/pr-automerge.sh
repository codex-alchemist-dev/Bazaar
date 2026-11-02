#!/usr/bin/env bash
# Enable squash auto-merge on open, non-draft pull requests into main, or merge them
# directly when auto-merge is unavailable and GitHub reports the PR as CLEAN.
#
# Usage: pr-automerge.sh [PR_NUMBER...]    (no arguments: every open PR into main)
#
# Safety rules (see .github/workflows/pr-automerge.yml):
#   - Never --admin. Branch protection and every required check still apply.
#   - A direct merge happens only when mergeStateStatus is CLEAN: every required check
#     passed, there is no conflict, and branch protection is satisfied.
#   - Skip drafts, PRs into other branches, and PRs with a CHANGES_REQUESTED review.
#   - The Signed-off-by check (pr-sponsor.yml) is a required check, so a PR without the
#     line never reaches CLEAN and never merges. This script does not add that line.
set -uo pipefail

repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is not set}"

if [ "$#" -gt 0 ]; then
  nums=("$@")
else
  mapfile -t nums < <(gh pr list -R "$repo" --state open --base main --limit 200 --json number --jq '.[].number')
fi

for n in "${nums[@]}"; do
  row=$(gh pr view "$n" -R "$repo" \
    --json isDraft,baseRefName,autoMergeRequest,mergeStateStatus,reviewDecision \
    --jq '[.isDraft, .baseRefName, (.autoMergeRequest != null), .mergeStateStatus, (.reviewDecision // "")] | @tsv') || {
    echo "::warning::#$n: could not read the pull request"
    continue
  }
  IFS=$'\t' read -r draft base auto state review <<<"$row"

  if [ "$draft" = true ] || [ "$base" != main ]; then
    continue
  fi
  if [ "$auto" = true ]; then
    continue
  fi
  if [ "$review" = CHANGES_REQUESTED ]; then
    echo "#$n: changes requested, left alone"
    continue
  fi

  if gh pr merge "$n" -R "$repo" --auto --squash >/dev/null 2>&1; then
    echo "#$n: auto-merge (squash) enabled"
  elif [ "$state" = CLEAN ]; then
    if gh pr merge "$n" -R "$repo" --squash >/dev/null 2>&1; then
      echo "#$n: merged (squash), all required checks were green"
    else
      echo "::warning::#$n: direct squash merge was refused"
    fi
  else
    echo "#$n: auto-merge unavailable and not CLEAN (merge state $state)"
  fi
done
