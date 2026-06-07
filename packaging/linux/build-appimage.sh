#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PUBLISH_DIR="$ROOT/publish/linux-x64"
APPDIR="$ROOT/dist/MicBeam.AppDir"
OUTPUT="$ROOT/dist/MicBeam-x86_64.AppImage"
APPIMAGETOOL="$ROOT/tools/appimagetool-x86_64.AppImage"

if [[ ! -f "$PUBLISH_DIR/CrossPlatformMicStreamer" ]]; then
  echo "Missing Linux publish output at: $PUBLISH_DIR/CrossPlatformMicStreamer"
  echo "Run one of these first from the repo root:"
  echo "  bash packaging/build-linux.sh"
  echo "  .\\packaging\\build-appimage.ps1   (Windows + WSL)"
  exit 1
fi

if [[ ! -f "$PUBLISH_DIR/libSkiaSharp.so" ]]; then
  echo "Missing native libraries in publish output."
  echo "Rebuild with PublishSingleFile=false so libSkiaSharp.so is included."
  exit 1
fi

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/share/applications"
mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -a "$PUBLISH_DIR"/. "$APPDIR/usr/bin/"

cp "$ROOT/packaging/linux/AppRun" "$APPDIR/AppRun"
cp "$ROOT/packaging/linux/MicBeam.desktop" "$APPDIR/MicBeam.desktop"
cp "$ROOT/packaging/linux/MicBeam.desktop" "$APPDIR/usr/share/applications/MicBeam.desktop"
cp "$ROOT/packaging/linux/MicBeam.png" "$APPDIR/MicBeam.png"
cp "$ROOT/packaging/linux/MicBeam.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/MicBeam.png"

sed -i 's/\r$//' "$APPDIR/AppRun" "$APPDIR/MicBeam.desktop" "$APPDIR/usr/share/applications/MicBeam.desktop"

chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/CrossPlatformMicStreamer"

if [[ ! -f "$APPIMAGETOOL" ]]; then
  mkdir -p "$ROOT/tools"
  echo "Downloading appimagetool..."
  curl -fsSL -o "$APPIMAGETOOL" \
    https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$APPIMAGETOOL"
fi

mkdir -p "$ROOT/dist"
ARCH=x86_64 "$APPIMAGETOOL" --appimage-extract-and-run "$APPDIR" "$OUTPUT"

echo
echo "Built: $OUTPUT"
ls -lh "$OUTPUT"
