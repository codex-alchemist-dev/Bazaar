#!/usr/bin/env bash
# Re-run the failed jobs once when the only failure is the Windows WebSocket test flake.
#
# For each open pull request into main, look at the latest CI run on its head branch.
# Re-run the failed jobs only when all of these hold:
#   - the run is for the current head commit and has finished with a failure;
#   - it is the first attempt (run_attempt == 1), so a re-run is never re-run again;
#   - every failed job is a Windows job;
#   - the failed-job log mentions WebSocket.
set -uo pipefail

repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is not set}"

mapfile -t rows < <(gh pr list -R "$repo" --state open --base main --limit 200 \
  --json number,headRefName,headRefOid,isCrossRepository \
  --jq '.[] | [.number, .headRefName, .headRefOid, .isCrossRepository] | @tsv')

for row in "${rows[@]}"; do
  IFS=$'\t' read -r n ref sha cross <<<"$row"
  [ "$cross" = true ] && continue

  run=$(gh run list -R "$repo" --workflow ci.yml --branch "$ref" --limit 1 \
    --json databaseId,headSha,status,conclusion \
    --jq '.[0] // empty | [.databaseId, .headSha, .status, .conclusion] | @tsv' 2>/dev/null) || continue
  [ -n "$run" ] || continue
  IFS=$'\t' read -r run_id run_sha status conclusion <<<"$run"

  [ "$run_sha" = "$sha" ] || continue
  [ "$status" = completed ] && [ "$conclusion" = failure ] || continue

  attempt=$(gh api "repos/$repo/actions/runs/$run_id" --jq '.run_attempt' 2>/dev/null) || continue
  [ "$attempt" = 1 ] || continue

  failed_jobs=$(gh run view "$run_id" -R "$repo" --json jobs \
    --jq '[.jobs[] | select(.conclusion == "failure") | .name] | join("\n")' 2>/dev/null) || continue
  [ -n "$failed_jobs" ] || continue
  if printf '%s\n' "$failed_jobs" | grep -qiv 'windows'; then
    echo "#$n: a non-Windows job failed, not re-running"
    continue
  fi

  if ! gh run view "$run_id" -R "$repo" --log-failed 2>/dev/null | grep -qi 'WebSocket'; then
    echo "#$n: failure is not the WebSocket flake, not re-running"
    continue
  fi

  if gh run rerun "$run_id" -R "$repo" --failed >/dev/null 2>&1; then
    echo "#$n: re-ran the failed Windows WebSocket job once (run $run_id)"
  else
    echo "::warning::#$n: re-run of run $run_id was refused"
  fi
done
