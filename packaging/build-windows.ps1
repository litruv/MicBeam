$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src/CrossPlatformMicStreamer/CrossPlatformMicStreamer.csproj"
$PublishDir = Join-Path $Root "publish/win-x64"
$DistDir = Join-Path $Root "dist"
$ZipPath = Join-Path $DistDir "MicBeam-win-x64.zip"

& (Join-Path $Root "packaging/generate-icons.ps1")

if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

Write-Host "Publishing self-contained Windows x64 build..."
dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir

if (-not (Test-Path (Join-Path $PublishDir "CrossPlatformMicStreamer.exe"))) {
    throw "Publish failed: CrossPlatformMicStreamer.exe not found in $PublishDir"
}

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

Write-Host "Creating zip..."
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "Windows release ready:"
Write-Host "  $ZipPath ($sizeMb MB)"
