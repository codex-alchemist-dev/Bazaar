#!/usr/bin/env bash
# Tests for dev-version.sh. Run: bash packaging/channels/test-dev-version.sh
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tool="$here/dev-version.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
fail=0
mk() { printf '<Project><PropertyGroup><VersionPrefix>%s</VersionPrefix></PropertyGroup></Project>\n' "$1" > "$tmp/$2"; }
mk 0.1.0 ok.props; mk 1.12.3 big.props; mk 0.1.0-beta.1 pre.props; mk 01.2.3 pad.props
printf '<Project/>\n' > "$tmp/none.props"

expect_ok() { # name expected_output args...
  local name="$1" want="$2"; shift 2
  local got; got="$(bash "$tool" "$@" 2>&1)" || { echo "FAIL $name: exit nonzero: $got"; fail=1; return; }
  [ "$got" = "$want" ] && echo "ok   $name" || { echo "FAIL $name: got '$got'"; fail=1; }
}
expect_fail() { # name args...
  local name="$1"; shift
  bash "$tool" "$@" >/dev/null 2>&1; local rc=$?
  [ "$rc" -eq 2 ] && echo "ok   $name" || { echo "FAIL $name: exit $rc"; fail=1; }
}

expect_ok basic $'semver=0.1.0-dev.42\nversion=0.1.0-dev.42+a1b2c3d' "$tmp/ok.props" 42 a1b2c3d4e5f6
expect_ok big   $'semver=1.12.3-dev.7\nversion=1.12.3-dev.7+abcdef0' "$tmp/big.props" 7 ABCDEF0123
expect_fail no-file "$tmp/missing.props" 1 a1b2c3d
expect_fail no-prefix "$tmp/none.props" 1 a1b2c3d
expect_fail prerelease-prefix "$tmp/pre.props" 1 a1b2c3d
expect_fail padded-prefix "$tmp/pad.props" 1 a1b2c3d
expect_fail zero-build "$tmp/ok.props" 0 a1b2c3d
expect_fail text-build "$tmp/ok.props" x a1b2c3d
expect_fail short-sha "$tmp/ok.props" 1 abc12
expect_fail non-hex-sha "$tmp/ok.props" 1 zzzzzzzz
exit "$fail"
