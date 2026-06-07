#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="$ROOT/branding/MicBeam.png"
ASSETS_DIR="$ROOT/src/CrossPlatformMicStreamer/Assets"
LINUX_ICON="$ROOT/packaging/linux/MicBeam.png"

if [[ ! -f "$SOURCE" ]]; then
  echo "Missing source icon: $SOURCE" >&2
  exit 1
fi

mkdir -p "$ASSETS_DIR"
cp "$SOURCE" "$ASSETS_DIR/MicBeam.png"

python3 - <<PY
from PIL import Image
from pathlib import Path

source = Path(r"$SOURCE")
assets = Path(r"$ASSETS_DIR")
linux = Path(r"$LINUX_ICON")

img = Image.open(source).convert("RGBA")
img.save(
    assets / "MicBeam.ico",
    format="ICO",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
img.resize((256, 256), Image.Resampling.LANCZOS).save(linux, format="PNG", optimize=True)
PY

echo "Generated:"
echo "  $ASSETS_DIR/MicBeam.png"
echo "  $ASSETS_DIR/MicBeam.ico"
echo "  $LINUX_ICON"
