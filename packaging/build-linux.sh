#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/CrossPlatformMicStreamer/CrossPlatformMicStreamer.csproj"
PUBLISH_DIR="$ROOT/publish/linux-x64"

bash "$ROOT/packaging/generate-icons.sh"

if [[ -d "$PUBLISH_DIR" ]]; then
  rm -rf "$PUBLISH_DIR"
fi

echo "Publishing self-contained Linux x64 build..."
dotnet publish "$PROJECT" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=false \
  -o "$PUBLISH_DIR"

bash "$ROOT/packaging/linux/build-appimage.sh"

echo
echo "Linux release ready:"
echo "  $ROOT/dist/MicBeam-x86_64.AppImage"
