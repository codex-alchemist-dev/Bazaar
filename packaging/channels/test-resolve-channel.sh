#!/usr/bin/env bash
# Tests for resolve-channel.sh. Run from any directory: bash packaging/channels/test-resolve-channel.sh
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$here/resolve-channel.sh"
failures=0

# expect <tag> <ring-or-empty> <expected-output-or-"ERR">
expect() {
  local tag="$1" ring="$2" want="$3" got status
  if [ -n "$ring" ]; then got="$("$script" "$tag" "$ring" 2>/dev/null)"; status=$?
  else got="$("$script" "$tag" 2>/dev/null)"; status=$?
  fi
  if [ "$want" = "ERR" ]; then
    if [ "$status" -ne 2 ]; then echo "FAIL $tag: expected exit 2, got $status"; failures=$((failures + 1)); fi
  else
    local expected; expected="$(printf '%s\n' "$want")"
    if [ "$status" -ne 0 ] || [ "$got" != "$expected" ]; then
      echo "FAIL $tag: expected [$want], got [$got] (exit $status)"; failures=$((failures + 1))
    fi
  fi
}

expect v0.1.0 ""              $'channel=stable\nring=all'
expect v2.4.10 ""             $'channel=stable\nring=all'
expect v0.2.0-alpha.4 ""      $'channel=alpha\nring=all'
expect v0.2.0-beta.1 ""       $'channel=beta\nring=all'
expect v0.2.0-rc.2 ""         $'channel=beta\nring=all'
expect v1.0.0 canary          $'channel=stable\nring=canary'
expect 0.1.0 ""               ERR
expect v1.0 ""                ERR
expect v01.0.0 ""             ERR
expect v1.0.0-dev.1 ""        ERR
expect v1.0.0-beta.1+abc ""   ERR
expect v1.0.0 "Bad Ring"      ERR

if [ "$failures" -eq 0 ]; then echo "all channel tests passed"; else echo "$failures failure(s)"; exit 1; fi
