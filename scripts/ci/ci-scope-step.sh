#!/usr/bin/env bash
# CI step: decide whether the Android or PG19 job runs its full steps for this commit.
#
# usage: ci-scope-step.sh <android|pg19>
# env:   GH_TOKEN, REPO, EVENT (github.event_name), REF (github.ref), SHA (github.sha)
# writes heavy=true|false to $GITHUB_OUTPUT.
#
# Full steps always run on main, on release tags, on a manual run and for a fork PR. For a
# branch push, the files changed against main come from the compare API and go through
# ci-scope.sh. Any failure, or a change list that may be truncated (300 files), runs the full job.
set -uo pipefail

job="${1:?job name (android or pg19)}"
out="${GITHUB_OUTPUT:-/dev/stdout}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

full() { echo "heavy=true" >> "$out"; echo "$job: full steps ($1)"; exit 0; }

[ "${EVENT:-}" = "push" ] || full "event ${EVENT:-unknown}"
case "${REF:-}" in
  refs/heads/main|refs/tags/*) full "main or a tag" ;;
esac

files="$(gh api --paginate "repos/$REPO/compare/main...$SHA" --jq '.files[].filename' 2>/dev/null)" || full "compare API failed"
count="$(printf '%s\n' "$files" | grep -c . || true)"
[ "$count" -ge 300 ] && full "$count changed files, list may be truncated"

result="$(printf '%s\n' "$files" | bash "$here/ci-scope.sh" "$job")"
echo "$result" >> "$out"
echo "$job: $result ($count changed files against main)"
