# MicBeam

Stream a mic from one machine to another over the network. No cloud, no account.

Typical setup: Steam Deck (or any Linux box) **Send** → your PC **Receive** into [VB-CABLE](https://vb-audio.com/Cable/) so Discord, OBS, or whatever sees it as a normal mic input.

MicBeam remembers your last peer, mode, devices, and format — it reconnects on launch.

## Install

**https://github.com/litruv/micbeam/releases**

- **Windows** — unzip, run `CrossPlatformMicStreamer.exe`. Allow UDP **18240** and TCP **18241** if the firewall asks.
- **Linux** — `chmod +x MicBeam-x86_64.AppImage && ./MicBeam-x86_64.AppImage` (needs `libfuse2` on some distros).

## Build

.NET 8 SDK required.

```bash
dotnet run --project src/CrossPlatformMicStreamer
```

Release packages:

```powershell
.\packaging\build-windows.ps1          # dist/MicBeam-win-x64.zip
.\packaging\build-appimage.ps1         # dist/MicBeam-x86_64.AppImage (Windows + WSL)
```

```bash
bash packaging/build-linux.sh          # same AppImage, native Linux
```
