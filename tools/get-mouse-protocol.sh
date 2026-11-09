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
# OpenMouse's app (also AGPL-3.0, https://github.com/OpenMouse-Project/openmouse): only its device-name → product
# picture table (src/shared/device-images.ts) is used, so Pulse shows the same pictures OpenMouse does.
APP_COMMIT="${OPENMOUSE_APP_COMMIT:-8885e938dcb1d84630c8bcf64abaea477f9e84ef}"
APP="$ROOT/.mouse-app"
rm -rf "$APP"; mkdir -p "$APP"
for try in 1 2 3 4 5; do
  (cd "$APP" && git init -q && git fetch -q --depth 1 https://github.com/OpenMouse-Project/openmouse "$APP_COMMIT" && git checkout -q FETCH_HEAD) && break
  echo "Fetch failed (try $try), retrying…"; sleep $((10 * try))
done
mkdir -p "$(dirname "$OUT")"
# esbuild reads the TypeScript directly; ES2019 so the script engine doesn't need the newest syntax.
BUILD="$ROOT/.mouse-build"
mkdir -p "$BUILD"
npm install --silent --no-audit --no-fund --prefix "$BUILD" esbuild@0.24.0
node "$ROOT/tools/mouse/bundle.mjs" "$BUILD" "$SRC" "$ROOT/tools/mouse/bridge.ts" "$OUT"
echo "openmouse.js: $(wc -c < "$OUT") bytes from mouse-protocol $VERSION"
