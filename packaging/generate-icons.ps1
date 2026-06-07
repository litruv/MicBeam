$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $Root "branding/MicBeam.png"
$AssetsDir = Join-Path $Root "src/CrossPlatformMicStreamer/Assets"
$LinuxIcon = Join-Path $Root "packaging/linux/MicBeam.png"
$AppIco = Join-Path $AssetsDir "MicBeam.ico"
$AppPng = Join-Path $AssetsDir "MicBeam.png"

if (-not (Test-Path $Source)) {
    throw "Missing source icon: $Source"
}

New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null
Copy-Item $Source $AppPng -Force

python -c @"
from PIL import Image
from pathlib import Path

source = Path(r'$Source')
assets = Path(r'$AssetsDir')
linux = Path(r'$LinuxIcon')

img = Image.open(source).convert('RGBA')
img.save(assets / 'MicBeam.ico', format='ICO', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
img.resize((256, 256), Image.Resampling.LANCZOS).save(linux, format='PNG', optimize=True)
"@

Write-Host "Generated:"
Write-Host "  $AppPng"
Write-Host "  $AppIco"
Write-Host "  $LinuxIcon"
