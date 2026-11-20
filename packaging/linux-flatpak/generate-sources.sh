#!/usr/bin/env bash
# Regenerates generated-sources.json: the offline NuGet sources for the Flatpak build (doc 30 section 3).
#
# Usage: packaging/linux-flatpak/generate-sources.sh [output-file]
#   output-file defaults to <this directory>/generated-sources.json.
#
# How: runs `dotnet restore` for the Linux head into a throwaway packages directory, once per runtime
# (linux-x64, linux-arm64), then emits one flatpak `file` source per .nupkg, using the nuget.org
# flat-container URL and the sha512 NuGet recorded for that package. Entries are de-duplicated and sorted,
# so the output is deterministic for a given dependency set. No third-party generator is used and nothing
# is fetched except through `dotnet restore` (which verifies packages against the repo's nuget.config).
#
# The operator supplies the .NET SDK from global.json on PATH (dotnet-install.sh into ~/.dotnet, with
# DOTNET_ROOT set). Set BAZAAR_FLATPAK_RIDS to override the runtimes (space separated).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="${1:-$here/generated-sources.json}"
rids="${BAZAAR_FLATPAK_RIDS:-linux-x64 linux-arm64}"
project="$repo/src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj"

die() { echo "generate-sources: $*" >&2; exit 1; }

command -v dotnet >/dev/null 2>&1 || die "dotnet is not on PATH. Install the SDK from global.json (dotnet-install.sh) and set DOTNET_ROOT and PATH."
command -v python3 >/dev/null 2>&1 || die "python3 is required"
[ -f "$project" ] || die "project not found: $project"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
packages="$tmp/packages"
mkdir -p "$packages"

for rid in $rids; do
  echo "generate-sources: restoring for $rid" >&2
  # Release + SelfContained + RuntimeIdentifier match the manifest's publish, so the runtime packs and the
  # ReadyToRun compiler pack (enabled by the csproj only when it sees Release and a RuntimeIdentifier
  # property; `--runtime` alone does not set it) are restored too.
  dotnet restore "$project" --packages "$packages" \
    -p:RuntimeIdentifier="$rid" -p:Configuration=Release -p:SelfContained=true \
    --verbosity quiet >&2
done

# Reads only the restored packages directory. python3 -I: standard library, no site or user paths.
python3 -I - "$packages" "$out" <<'PY'
import base64, json, os, sys

packages, out = sys.argv[1], sys.argv[2]
entries = {}
for pid in sorted(os.listdir(packages)):
    pdir = os.path.join(packages, pid)
    if not os.path.isdir(pdir):
        continue
    for ver in sorted(os.listdir(pdir)):
        base = "%s.%s" % (pid, ver)
        nupkg = os.path.join(pdir, ver, base + ".nupkg")
        sha = nupkg + ".sha512"
        if not os.path.isfile(nupkg):
            continue
        if not os.path.isfile(sha):
            sys.exit("generate-sources: missing %s.sha512" % base)
        with open(sha, encoding="ascii") as f:
            digest = base64.b64decode(f.read().strip()).hex()
        if len(digest) != 128:
            sys.exit("generate-sources: bad sha512 for %s" % base)
        # The package cache directories are already the lower-case normalized id and version
        # that the flat container serves.
        entries[base + ".nupkg"] = {
            "type": "file",
            "url": "https://api.nuget.org/v3-flatcontainer/%s/%s/%s.nupkg" % (pid, ver, base),
            "sha512": digest,
            "dest": "nuget-sources",
            "dest-filename": base + ".nupkg",
        }
if not entries:
    sys.exit("generate-sources: restore produced no packages")
ordered = [entries[k] for k in sorted(entries)]
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(ordered, f, indent=2)
    f.write("\n")
print("generate-sources: wrote %d entries to %s" % (len(ordered), out), file=sys.stderr)
PY
