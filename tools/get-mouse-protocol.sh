#!/usr/bin/env bash
# Builds Assets/Mouse/openmouse.js: OpenMouse's mouse protocol library (AGPL-3.0,
# https://github.com/OpenMouse-Project/mouse-protocol) bundled with Pulse's bridge (tools/mouse/bridge.ts) into one
# script Pulse runs in its built-in script engine. Pinned to a release so builds are repeatable.
set -euo pipefail
VERSION="${MOUSE_PROTOCOL_VERSION:-v0.58.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/.mouse-protocol"
OUT="$ROOT/src/PulseOptimizer/Assets/Mouse/openmouse.js"
rm -rf "$SRC"
for try in 1 2 3 4 5; do
  git clone -q --depth 1 --branch "$VERSION" https://github.com/OpenMouse-Project/mouse-protocol "$SRC" && break
  echo "Clone failed (try $try), retrying…"; sleep $((10 * try))
done
mkdir -p "$(dirname "$OUT")"
# esbuild reads the TypeScript directly; ES2019 so the script engine doesn't need the newest syntax.
npx --yes esbuild@0.24.0 "$ROOT/tools/mouse/bridge.ts" --bundle --format=iife --target=es2019 --platform=neutral --minify \
  --legal-comments=none --outfile="$OUT"
echo "openmouse.js: $(wc -c < "$OUT") bytes from mouse-protocol $VERSION"
