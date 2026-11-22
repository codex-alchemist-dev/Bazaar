#!/usr/bin/env bash
# Tests for lts-branch.sh. Run from any directory: bash packaging/channels/test-lts-branch.sh
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$here/lts-branch.sh"
failures=0

# expect <expected-output-or-"ERR"> <args...>
expect() {
  local want="$1"; shift
  local got status
  got="$("$script" "$@" 2>/dev/null)"; status=$?
  if [ "$want" = "ERR" ]; then
    if [ "$status" -ne 2 ]; then echo "FAIL $*: expected exit 2, got $status"; failures=$((failures + 1)); fi
  elif [ "$status" -ne 0 ] || [ "$got" != "$(printf '%s\n' "$want")" ]; then
    echo "FAIL $*: expected [$want], got [$got] (exit $status)"; failures=$((failures + 1))
  fi
}

# classify
expect $'kind=ltsc\ncodename=zuhal\nmajor=1'    classify ltsc/zuhal
expect $'kind=ltsc\ncodename=yliaster\nmajor=2' classify refs/heads/ltsc/yliaster
expect "kind=other"                              classify main
expect "kind=other"                              classify agent/ltsc-branch-handling
expect ERR                                       classify ltsc/nonsense
expect ERR                                       classify ltsc/Zuhal

# check: valid patch-only bumps
expect $'channel=ltsc\nring=all\nline=1.4\ncodename=zuhal\nmajor=1'     check ltsc/zuhal v1.4.2 v1.4.0
expect $'channel=ltsc\nring=canary\nline=1.0\ncodename=zuhal\nmajor=1'  check ltsc/zuhal v1.0.1 v1.0.0 canary
expect $'channel=ltsc\nring=all\nline=2.1\ncodename=yliaster\nmajor=2'  check origin/ltsc/yliaster v2.1.5 v2.1.3

# check: fail closed
expect ERR check main v1.4.2 v1.4.0                 # not an LTSC branch
expect ERR check release/lts-1.4 v1.4.2 v1.4.0      # other naming is not recognised
expect ERR check ltsc/zuhal v1.5.0 v1.4.0           # MINOR bump
expect ERR check ltsc/zuhal v2.0.0 v1.4.0           # MAJOR bump
expect ERR check ltsc/zuhal v1.4.0 v1.4.0           # same patch as fork
expect ERR check ltsc/zuhal v1.4.1 v1.4.2           # patch below fork
expect ERR check ltsc/zuhal v1.4.2-beta.1 v1.4.0    # pre-release suffix
expect ERR check ltsc/zuhal v1.4.2-lts v1.4.0       # no -lts tag form
expect ERR check ltsc/zuhal 1.4.2 v1.4.0            # no v prefix
expect ERR check ltsc/yliaster v1.4.2 v1.4.0        # tag major != branch major
expect ERR check ltsc/zuhal v1.4.2 v2.4.0           # fork tag from another major
expect ERR check ltsc/zuhal v1.4.2 v1.4.0 "Bad Ring"
expect ERR check ltsc/zuhal v1.4.2                  # fork tag missing
expect ERR bogus

# The codename table must match src/Bazaar.Contracts/ProductVersion.cs
cs="$here/../../src/Bazaar.Contracts/ProductVersion.cs"
checked=0
if [ -f "$cs" ]; then
  while IFS= read -r line; do
    if [[ "$line" =~ \[([0-9]+)\][[:space:]]*=[[:space:]]*\"([A-Za-z]+)\" ]]; then
      n="${BASH_REMATCH[1]}"; name="$(printf '%s' "${BASH_REMATCH[2]}" | tr 'A-Z' 'a-z')"
      [ "$n" = "0" ] && continue
      checked=$((checked + 1))
      got="$("$script" classify "ltsc/$name" 2>/dev/null | sed -n 's/^major=//p')"
      if [ "$got" != "$n" ]; then echo "FAIL codename table: $name expected major $n, got [$got]"; failures=$((failures + 1)); fi
    fi
  done < "$cs"
  [ "$checked" -gt 0 ] || { echo "FAIL codename table: no codenames parsed from ProductVersion.cs"; failures=$((failures + 1)); }
fi

if [ "$failures" -eq 0 ]; then echo "all lts-branch tests passed"; else echo "$failures failure(s)"; exit 1; fi
