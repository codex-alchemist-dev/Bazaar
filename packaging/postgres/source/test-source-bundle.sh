#!/usr/bin/env bash
# Checks a staged source-built bundle (one <major> folder from build-from-source.sh or build-from-source-windows.ps1).
# Exits non-zero, with a message per failure, when the bundle does not match the pins in sources.txt or is missing a file
# the host probes for. It runs in CI on Linux and on Windows (Git Bash), and does not start a database (see
# smoke-throwaway-cluster.sh for that).
#
#   test-source-bundle.sh --bundle artifacts/pg-src-bundle/18 --major 18 --platform linux|windows
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bundle=""
major=""
platform=""
while [ $# -gt 0 ]; do
  case "$1" in
    --bundle) bundle="$2"; shift 2 ;;
    --major) major="$2"; shift 2 ;;
    --platform) platform="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "$bundle" ] && [ -n "$major" ] || { echo "usage: --bundle DIR --major 18|19 --platform linux|windows" >&2; exit 2; }
case "$platform" in linux|windows) ;; *) echo "--platform must be linux or windows" >&2; exit 2 ;; esac

# shellcheck disable=SC1091
. "$here/sources.txt"
case "$major" in
  18) pg_version="$PG18_VERSION"; pg_sha="$PG18_SHA256" ;;
  19) pg_version="$PG19_VERSION"; pg_sha="$PG19_SHA256" ;;
  *) echo "unsupported major '$major'" >&2; exit 2 ;;
esac

failures=()
expect() { if [ "$1" != "0" ]; then failures+=("$2"); fi; }

# 1. The manifest records the major, the versions and the input hashes, all equal to the pins.
manifest="$bundle/bazaar-bundle.txt"
if [ ! -f "$manifest" ]; then
  failures+=("bundle has no bazaar-bundle.txt")
else
  get() { sed -n "s/^$1=//p" "$manifest" | head -1; }
  expect "$([ "$(get pg_major)" = "$major" ] && echo 0 || echo 1)" "manifest pg_major is '$(get pg_major)', expected $major"
  expect "$([ "$(get pg_version)" = "$pg_version" ] && echo 0 || echo 1)" "manifest pg_version is '$(get pg_version)', expected $pg_version"
  expect "$([ "$(get pgvector)" = "$PGVECTOR_VERSION" ] && echo 0 || echo 1)" "manifest pgvector is '$(get pgvector)', expected $PGVECTOR_VERSION"
  expect "$([ "$(get pg_source_sha256)" = "$(echo "$pg_sha" | tr 'A-F' 'a-f')" ] && echo 0 || echo 1)" "manifest pg_source_sha256 does not match sources.txt"
  expect "$([ "$(get pgvector_tarball_sha256)" = "$(echo "$PGVECTOR_SHA256" | tr 'A-F' 'a-f')" ] && echo 0 || echo 1)" "manifest pgvector_tarball_sha256 does not match sources.txt"
  expect "$([ "$(get build_path)" = "source" ] && echo 0 || echo 1)" "manifest build_path is not 'source'"
  signed="$(get signed)"
  expect "$([ "$signed" = "false" ] || [ "$signed" = "true" ] && echo 0 || echo 1)" "manifest signed must be true or false"
fi

# 2. The files the host probes for at startup, named per platform.
# Linux keeps PostgreSQL's relocatable layout (lib/postgresql, share/postgresql), because the server finds its
# extension files relative to bin/. Windows uses the flat layout (lib, share/extension), as the EDB bundle does.
if [ "$platform" = "linux" ]; then
  exe_suffix=""
  vector_lib="lib/postgresql/vector.so"
  ext_dir="share/postgresql/extension"
else
  exe_suffix=".exe"
  vector_lib="lib/vector.dll"
  ext_dir="share/extension"
fi
required=("bin/postgres$exe_suffix" "bin/initdb$exe_suffix" "bin/pg_ctl$exe_suffix" "$vector_lib" "$ext_dir/vector.control")
for rel in "${required[@]}"; do
  [ -f "$bundle/$rel" ] || failures+=("bundle is missing $rel")
done
if ! compgen -G "$bundle/$ext_dir/vector--*.sql" >/dev/null; then
  failures+=("bundle has no pgvector SQL scripts in $ext_dir")
fi
[ -e "$bundle/include" ] && failures+=("bundle ships development headers (include/)")

# 3. SHA256SUMS covers every file in the bundle and every line verifies.
sums="$bundle/SHA256SUMS"
if [ ! -f "$sums" ]; then
  failures+=("bundle has no SHA256SUMS")
else
  if ! (cd "$bundle" && sha256sum -c --quiet SHA256SUMS >/dev/null 2>&1); then
    failures+=("SHA256SUMS does not verify")
  fi
  listed=$(wc -l < "$sums")
  present=$(cd "$bundle" && find . -type f ! -name SHA256SUMS | wc -l)
  [ "$listed" -eq "$present" ] || failures+=("SHA256SUMS lists $listed files, the bundle has $present")
fi

if [ "${#failures[@]}" -gt 0 ]; then
  printf 'FAIL: %s\n' "${failures[@]}" >&2
  exit 1
fi
echo "Source bundle OK: $bundle (PostgreSQL $pg_version, $platform)."
