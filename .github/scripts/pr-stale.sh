#!/usr/bin/env bash
# Flag open pull requests with no activity for 24 hours, and close true duplicates.
#
# "Activity" means a commit pushed to the branch, a review, or a comment by a person
# (comments posted by github-actions are ignored, so this script does not count itself).
# Label changes do not count either, so labelling a PR does not reset its clock.
#
# For each open PR with no activity for 24 hours:
#   - add the `stale` label and post one comment, the first time only;
#   - if it also carries the `duplicate` label, close it with a comment. Nothing is closed
#     without that label, so a person (or a thread) decides what is a duplicate.
# A stale PR that has activity again loses the `stale` label.
set -uo pipefail

repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is not set}"
now=$(date -u +%s)
limit=$((24 * 60 * 60))
footer=$'\n\n---\n_Posted by the stale pull request report (`.github/workflows/pr-stale.yml`)_'

gh label create stale -R "$repo" --color FBCA04 \
  --description "No activity for 24 hours; the owning thread should merge, update or close it" >/dev/null 2>&1 || true
gh label create duplicate -R "$repo" --color CFD3D7 \
  --description "Duplicates another open pull request; closed automatically after 24 hours idle" >/dev/null 2>&1 || true

mapfile -t nums < <(gh pr list -R "$repo" --state open --limit 200 --json number --jq '.[].number')

for n in "${nums[@]}"; do
  row=$(gh pr view "$n" -R "$repo" --json commits,comments,reviews,labels \
    --jq '{
      last_commit: ([.commits[].committedDate] | max // ""),
      last_comment: ([.comments[] | select(.author.login != "github-actions") | .createdAt] | max // ""),
      last_review: ([.reviews[] | .submittedAt] | max // ""),
      labels: [.labels[].name]
    } | [([.last_commit, .last_comment, .last_review] | max // ""), (.labels | join(","))] | @tsv') || {
    echo "::warning::#$n: could not read the pull request"
    continue
  }
  IFS=$'\t' read -r last labels <<<"$row"

  last_epoch=0
  if [ -n "$last" ]; then
    last_epoch=$(date -u -d "$last" +%s 2>/dev/null || echo 0)
  fi
  idle=$((now - last_epoch))

  has_label() { [[ ",$labels," == *",$1,"* ]]; }

  if [ "$idle" -lt "$limit" ]; then
    if has_label stale; then
      gh pr edit "$n" -R "$repo" --remove-label stale >/dev/null 2>&1 && echo "#$n: active again, stale label removed"
    fi
    continue
  fi

  if has_label duplicate; then
    gh pr close "$n" -R "$repo" --comment "Closing: this pull request is labelled \`duplicate\` and has had no activity for 24 hours.${footer}" >/dev/null 2>&1 \
      && echo "#$n: closed as a duplicate" \
      || echo "::warning::#$n: could not close the duplicate"
    continue
  fi

  if ! has_label stale; then
    gh pr edit "$n" -R "$repo" --add-label stale >/dev/null 2>&1 || echo "::warning::#$n: could not add the stale label"
    gh pr comment "$n" -R "$repo" --body "This pull request has had no activity for 24 hours. Its owning thread should merge it, push to it or close it. Until the owner acts, it stays labelled \`stale\`.${footer}" >/dev/null 2>&1 \
      && echo "#$n: flagged stale and commented" \
      || echo "::warning::#$n: could not post the stale comment"
  fi
done
