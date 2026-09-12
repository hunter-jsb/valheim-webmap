#!/usr/bin/env bash
# Build the Thunderstore upload zip (dist/WebMap-<version>.zip) from manifest.json.
set -euo pipefail
cd "$(dirname "$0")"
V=$(python3 -c "import json;print(json.load(open('manifest.json'))['version_number'])")
dotnet build WebMap/WebMap.csproj -c Release -v minimal
# web/main.js is a build product and is gitignored, so a fresh clone has none:
# packaging without this shipped a viewer with no script at all.
npm run build
rm -rf dist/pkg "dist/WebMap-$V.zip"
mkdir -p dist/pkg
cp manifest.json README.md CHANGELOG.md icon.png LICENSE dist/pkg/
cp WebMap/bin/Release/WebMap.dll WebMap/bin/Release/websocket-sharp.dll dist/pkg/
cp -r WebMap/web dist/pkg/web
[ -s dist/pkg/web/main.js ] || { echo "web/main.js missing or empty" >&2; exit 1; }
(cd dist/pkg && zip -qr "../WebMap-$V.zip" . -x '.*')
echo "dist/WebMap-$V.zip"
