# MicBeam

Stream your mic from one PC to another over the network. No cloud. No accounts. No bullshit.

<img width="432" height="457" alt="image" src="https://github.com/user-attachments/assets/f6a4b044-3fbc-45dd-9056-966ecb70edbb" />

Typical setup: any PC **Send Mic** → your main PC **Receive**, piping into [VB-CABLE](https://vb-audio.com/Cable/) so Discord, OBS, games, whatever just sees it as a normal microphone.

It remembers your last peer, mode, devices, and format, so it just reconnects on launch instead of making you set it up every time.

## Install

**[https://github.com/litruv/micbeam/releases](https://github.com/litruv/micbeam/releases)**

* **Windows** - unzip, run `CrossPlatformMicStreamer.exe`. Allow on Windows Firewall if it asks.
* **Linux** - `chmod +x MicBeam-x86_64.AppImage && ./MicBeam-x86_64.AppImage` (you may need `libfuse2` depending on distro)
   - use something like [AppImageLauncher](https://github.com/TheAssassin/AppImageLauncher) to make it easier to launch later

## Build

Requires .NET 8 SDK.

```bash
dotnet run --project src/CrossPlatformMicStreamer
```

### Packaging

```powershell
.\packaging\build-windows.ps1      # dist/MicBeam-win-x64.zip
.\packaging\build-appimage.ps1     # AppImage build (Windows + WSL works)
```

```bash
bash packaging/build-linux.sh      # native Linux build, same AppImage output
```
