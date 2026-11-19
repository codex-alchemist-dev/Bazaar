#!/usr/bin/env bash
# Builds one packaged PostgreSQL bundle from pinned upstream source on Linux: PostgreSQL (18 or 19 candidate) plus
# pgvector, compiled with the host toolchain and installed into a private prefix. It is the source-compile path of
# doc 42 section 14.1 (Q1 path a). The upstream-binary path (versions.txt, EDB zip) is separate and unchanged.
#
#   packaging/postgres/source/build-from-source.sh --major 18 --out artifacts/pg-src-bundle [--work DIR] [--jobs N]
#
# Output (one folder per major, same shape as the Windows bundle so the host probes the same paths):
#   <out>/<major>/bin, lib, share      server and client tools, vector.so, extension files
#   <out>/<major>/bazaar-bundle.txt    keys: pg_major, pg_version, pgvector, pg_source_sha256, pgvector_tarball_sha256,
#                                      build_path, toolchain, signed
#   <out>/<major>/SHA256SUMS           SHA-256 of every file in the bundle, relative to <major>/
#
# Nothing here is downloaded except the three pinned tarballs. Each is checked against sources.txt before it is
# unpacked. The build writes only under --work and --out. It never touches the system PostgreSQL.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
major=""
out=""
work=""
jobs="$(nproc 2>/dev/null || echo 2)"

usage() { sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-2}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --major) major="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --work) work="$2"; shift 2 ;;
    --jobs) jobs="$2"; shift 2 ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done
[ -n "$major" ] && [ -n "$out" ] || usage

# shellcheck disable=SC1091
. "$here/sources.txt"
case "$major" in
  18) pg_version="$PG18_VERSION"; pg_tar="$PG18_TARBALL"; pg_url="$PG18_URL"; pg_sha="$PG18_SHA256" ;;
  19) pg_version="$PG19_VERSION"; pg_tar="$PG19_TARBALL"; pg_url="$PG19_URL"; pg_sha="$PG19_SHA256" ;;
  *) echo "unsupported major '$major' (supported: 18, 19)" >&2; exit 2 ;;
esac

# A pin must be a 64-digit hex SHA-256 before anything is downloaded against it.
for value in "$pg_sha" "$PGVECTOR_SHA256"; do
  if ! printf '%s' "$value" | grep -Eq '^[0-9A-Fa-f]{64}$'; then
    echo "a SHA-256 pin in sources.txt is not 64 hex digits" >&2
    exit 2
  fi
done

work="${work:-$(mktemp -d "${TMPDIR:-/tmp}/bazaar-pg-src.XXXXXX")}"
mkdir -p "$work/dl" "$out"
src="$work/pg-$major"
prefix="$work/prefix-$major"
rm -rf "$src" "$prefix"
mkdir -p "$src"

# fetch <url> <file> <expected-sha256>: download once, verify, delete on mismatch.
fetch() {
  local url="$1" file="$2" expected="$3"
  if [ ! -f "$file" ]; then
    curl --fail --location --silent --show-error --retry 4 --output "$file.part" "$url"
    mv "$file.part" "$file"
  fi
  local actual
  actual="$(sha256sum "$file" | cut -d' ' -f1)"
  if [ "${actual,,}" != "${expected,,}" ]; then
    rm -f "$file"
    echo "SHA-256 mismatch for $(basename "$file"): expected $expected, got $actual" >&2
    exit 1
  fi
  echo "verified $(basename "$file") $actual"
}

fetch "$pg_url" "$work/dl/$pg_tar" "$pg_sha"
fetch "$PGVECTOR_URL" "$work/dl/$PGVECTOR_TARBALL" "$PGVECTOR_SHA256"

# PostgreSQL: configure into a private prefix, then build and install the server and client tools.
tar -xzf "$work/dl/$pg_tar" -C "$src" --strip-components=1
(
  cd "$src"
  ./configure --prefix="$prefix" --without-readline --without-icu --with-openssl --with-zlib >"$work/configure-$major.log" 2>&1 \
    || { tail -40 "$work/configure-$major.log" >&2; exit 1; }
  make -j"$jobs" >"$work/make-$major.log" 2>&1 || { tail -40 "$work/make-$major.log" >&2; exit 1; }
  make install >"$work/install-$major.log" 2>&1 || { tail -40 "$work/install-$major.log" >&2; exit 1; }
)

# pgvector: built against this PostgreSQL's pg_config, so the library matches the major it ships with.
pgv="$work/pgvector-$PGVECTOR_VERSION"
rm -rf "$pgv"
mkdir -p "$pgv"
tar -xzf "$work/dl/$PGVECTOR_TARBALL" -C "$pgv" --strip-components=1
(
  cd "$pgv"
  make -j"$jobs" PG_CONFIG="$prefix/bin/pg_config" >"$work/pgvector-make-$major.log" 2>&1 \
    || { tail -40 "$work/pgvector-make-$major.log" >&2; exit 1; }
  make install PG_CONFIG="$prefix/bin/pg_config" >"$work/pgvector-install-$major.log" 2>&1 \
    || { tail -40 "$work/pgvector-install-$major.log" >&2; exit 1; }
)

# Stage: keep what the host needs, drop headers and documentation (the build used them).
# Layout: PostgreSQL installs its libraries in lib/postgresql and its extension files in share/postgresql/extension.
# The server finds them relative to bin/, so the bundle keeps that layout (the Windows bundle is flat instead).
dest="$out/$major"
rm -rf "$dest"
mkdir -p "$dest"
cp -a "$prefix/bin" "$prefix/lib" "$prefix/share" "$dest/"
rm -rf "$dest/lib/pkgconfig" "$dest/share/man" "$dest/share/doc"
# Shared libraries that were compiled in the staging prefix keep RPATH pointing at it; the host sets LD_LIBRARY_PATH
# to <major>/lib at run time (the same contract as the Windows bundle, where DLLs sit beside the server).

toolchain="$(gcc --version 2>/dev/null | head -1 || echo unknown)"
{
  echo "pg_major=$major"
  echo "pg_version=$pg_version"
  echo "pgvector=$PGVECTOR_VERSION"
  echo "pg_source_sha256=$(echo "$pg_sha" | tr 'A-F' 'a-f')"
  echo "pgvector_tarball_sha256=$(echo "$PGVECTOR_SHA256" | tr 'A-F' 'a-f')"
  echo "build_path=source"
  echo "toolchain=$toolchain"
  echo "signed=false"
} > "$dest/bazaar-bundle.txt"

(cd "$dest" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)
echo "bundle staged at $dest (PostgreSQL $pg_version, pgvector $PGVECTOR_VERSION, source build)"
