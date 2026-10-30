#!/usr/bin/env bash
# Fetches and builds the pinned FreeLLMAPI release that the Bazaar host runs as its managed sidecar.
# Usage: scripts/freellmapi/install.sh [install-dir]   (default: /var/lib/bazaar/freellmapi/app)
# Needs git, Node.js 20 to 24 and npm. Upgrades are an owner action: change the tag below and FreeLlmApiSidecarOptions.PinnedVersion together.
set -euo pipefail

readonly FREELLMAPI_TAG="v0.16.13"
readonly FREELLMAPI_REPO="https://github.com/tashfeenahmed/freellmapi.git"
readonly INSTALL_DIR="${1:-/var/lib/bazaar/freellmapi/app}"

if [[ -f "$INSTALL_DIR/server/dist/index.js" ]]; then
  echo "FreeLLMAPI $FREELLMAPI_TAG is already built at $INSTALL_DIR"
  exit 0
fi

node_major="$(node -p 'process.versions.node.split(".")[0]')"
if (( node_major < 20 || node_major > 24 )); then
  echo "Node.js 20 to 24 is required for FreeLLMAPI; found major version $node_major." >&2
  exit 1
fi

rm -rf "$INSTALL_DIR"
mkdir -p "$(dirname "$INSTALL_DIR")"
git clone --depth 1 --branch "$FREELLMAPI_TAG" "$FREELLMAPI_REPO" "$INSTALL_DIR"
cd "$INSTALL_DIR"
npm ci --no-audit --no-fund
npm run build
npm prune --omit=dev
echo "FreeLLMAPI $FREELLMAPI_TAG built at $INSTALL_DIR"
