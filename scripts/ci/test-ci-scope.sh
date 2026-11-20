#!/usr/bin/env bash
# Tests for ci-scope.sh. Run: bash scripts/ci/test-ci-scope.sh
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail=0
check() { # name job want files...
  local name="$1" job="$2" want="$3"; shift 3
  local got; got="$(printf '%s\n' "$@" | bash "$here/ci-scope.sh" "$job")"
  [ "$got" = "heavy=$want" ] && echo "ok   $name" || { echo "FAIL $name: got '$got'"; fail=1; }
}
check android-docs-only      android false docs/pr-flow.md README.md
check android-server-only    android false src/Bazaar.Server.Git/X.cs tests/Bazaar.Server.Git.Tests/Y.cs
check android-client-change  android true  src/Bazaar.Client.Android/A.cs docs/x.md
check android-shared-change  android true  src/Bazaar.Contracts/C.cs
check android-ci-yml         android true  .github/workflows/ci.yml
check android-props          android true  Directory.Packages.props
check pg19-docs-only         pg19 false docs/a.md
check pg19-client-only       pg19 false src/Bazaar.Client.Windows/W.cs tests/Bazaar.Client.Windows.Tests/T.cs
check pg19-server-change     pg19 true  src/Bazaar.Server/Data/Sql/a.sql
check pg19-migration         pg19 true  src/Bazaar.Server.Host/Sql/components/x/0001.sql docs/a.md
check pg19-module            pg19 true  src/Bazaar.Rubedo/R.cs
check pg19-ci-yml            pg19 true  .github/workflows/ci.yml
got="$(printf '' | bash "$here/ci-scope.sh" android)"
[ "$got" = "heavy=true" ] && echo "ok   empty-list-is-heavy" || { echo "FAIL empty: $got"; fail=1; }
got="$(printf 'docs/a.md\n' | bash "$here/ci-scope.sh" nonsense 2>/dev/null)"
[ "$got" = "heavy=true" ] && echo "ok   unknown-job-is-heavy" || { echo "FAIL unknown: $got"; fail=1; }
exit "$fail"
