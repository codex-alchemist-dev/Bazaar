#!/usr/bin/env bash
# Keep open pull requests into main up to date: merge main into any PR branch that is behind.
#
# It uses GitHub's update-branch API, which makes a merge commit (never a force push), and
# it sends expected_head_sha so a branch that moved in the meantime is not overwritten.
#
# A push made with GITHUB_TOKEN does not start workflows. So after each update this script
# starts the required checks itself with workflow_dispatch (ci.yml and pr-sponsor.yml).
# Without that, the new head commit would never get its required checks.
#
# Forks and branches that conflict with main are skipped and reported.
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

  if gh api -X PUT "repos/$repo/pulls/$n/update-branch" -f expected_head_sha="$sha" >/dev/null 2>&1; then
    echo "#$n: merged main into $ref ($behind commits behind)"
    gh workflow run ci.yml -R "$repo" --ref "$ref" || echo "::warning::#$n: could not start ci.yml"
    gh workflow run pr-sponsor.yml -R "$repo" --ref "$ref" -f pr_number="$n" || echo "::warning::#$n: could not start pr-sponsor.yml"
  else
    echo "::warning::#$n: update-branch refused (conflict or the branch moved); needs its owning thread"
  fi
done
