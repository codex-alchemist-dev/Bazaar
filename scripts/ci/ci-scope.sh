#!/usr/bin/env bash
# Decides whether an expensive CI job has work to do for a change.
#
# usage: ci-scope.sh <android|pg19>     (changed file paths, one per line, on stdin)
# prints: heavy=true   run the job's full steps
#         heavy=false  every changed file is known not to affect this job; skip its steps
#
# Fails safe: an empty list, an unknown job name, or any path not listed below means heavy=true.
# The job still runs and reports success when it skips, so its required check name is unchanged.
set -euo pipefail

job="${1:-}"
case "$job" in
  android)
    # Paths that cannot change the Android head or its gate: docs, the server side, packaging.
    skip_re='^(docs/|packaging/|src/Bazaar\.Server|tests/Bazaar\.Server)|\.md$|^LICENSE'
    ;;
  pg19)
    # Paths that cannot change what runs against PostgreSQL: docs and the client side.
    skip_re='^(docs/|src/Bazaar\.Client|tests/Bazaar\.Client|scripts/ci/android)|\.md$|^LICENSE'
    ;;
  *)
    echo "ci-scope: unknown job '$job' (expected android or pg19)" >&2
    echo "heavy=true"
    exit 0
    ;;
esac

seen=0
while IFS= read -r path; do
  [ -z "$path" ] && continue
  seen=1
  if ! [[ "$path" =~ $skip_re ]]; then
    echo "heavy=true"
    exit 0
  fi
done

if [ "$seen" -eq 0 ]; then
  echo "heavy=true"
else
  echo "heavy=false"
fi
