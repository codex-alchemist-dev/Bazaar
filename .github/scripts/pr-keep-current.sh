#!/usr/bin/env bash
# Report open pull requests that are behind main. It does not change any branch.
#
# Why it does not update branches: a push made with GITHUB_TOKEN (what update-branch makes)
# starts no pull_request workflows, and on those heads the CodeQL pull_request run ends as
# action_required. The required Analyze checks then never report, so the PR cannot merge,
# and the next hourly run moves main again. That loop blocks merges (seen on #374).
#
# Branch protection on main does not require PRs to be up to date with main
# (strict_required_status_checks_policy is false), so a behind PR can still merge once its
# own required checks pass. Nothing needs the update, so this script only reports.
#
# A PR that does need main merged must be updated by its owning thread, with a commit pushed
# by a person or an App token, so its pull_request checks run.
set -uo pipefail

repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is not set}"

mapfile -t rows < <(gh pr list -R "$repo" --state open --base main --limit 200 \
  --json number,headRefName,headRefOid,isCrossRepository \
  --jq '.[] | [.number, .headRefName, .headRefOid, .isCrossRepository] | @tsv')

for row in "${rows[@]}"; do
  IFS=$'\t' read -r n ref sha cross <<<"$row"

  if [ "$cross" = true ]; then
    echo "#$n: from a fork, left alone"
    continue
  fi

  behind=$(gh api "repos/$repo/compare/main...$ref" --jq '.behind_by' 2>/dev/null) || {
    echo "::warning::#$n: could not compare $ref with main"
    continue
  }
  if [ "$behind" = 0 ]; then
    continue
  fi

  echo "::notice::#$n: $ref is $behind commits behind main; not updated (see pr-keep-current.sh)"
done
