$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $Root "publish/linux-x64"

& (Join-Path $Root "packaging/generate-icons.ps1")

Write-Host "Publishing self-contained Linux x64 build..."
dotnet publish (Join-Path $Root "src/CrossPlatformMicStreamer/CrossPlatformMicStreamer.csproj") `
    -c Release `
    -r linux-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir

$Drive = $Root.Substring(0, 1).ToLower()
$WslRoot = "/mnt/$Drive" + ($Root.Substring(2) -replace '\\', '/')
$WslScript = "$WslRoot/packaging/linux/build-appimage.sh"

Write-Host "Building AppImage in WSL..."
wsl bash -lc "sed -i 's/\r$//' '$WslScript' && bash '$WslScript'"

$AppImage = Join-Path $Root "dist/MicBeam-x86_64.AppImage"
if (Test-Path $AppImage) {
    Write-Host ""
    Write-Host "AppImage ready:"
    Write-Host "  $AppImage"
}
