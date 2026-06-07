# MicBeam

Use a mic on one machine, hear it on another. MicBeam captures your microphone and sends it over the network as uncompressed PCM - handy when you want your Steam Deck mic on your Windows PC, or any other Windows/Linux combo on the same LAN (or Tailscale).

No accounts, no cloud relay. Pick a peer, pick Send or Receive, pick your audio device.

## Install

Download the latest release:

**https://github.com/litruv/micbeam/releases**

### Windows

Grab the Windows zip, unpack, run `CrossPlatformMicStreamer.exe`.

Allow UDP **18240** and TCP **18241** through the firewall if Windows prompts you.

### Linux

Grab `MicBeam-x86_64.AppImage`, make it executable, run it:

```bash
chmod +x MicBeam-x86_64.AppImage
./MicBeam-x86_64.AppImage
```

If FUSE isn't installed:

```bash
sudo apt install libfuse2
./MicBeam-x86_64.AppImage --appimage-extract-and-run
```

## What it does

**Send** - stream from a local microphone to whoever you're connected to.

**Receive** - play incoming audio on a local speaker or headphones.

One side sends, the other receives. Deck streams the mic, PC plays it - that’s the usual setup.

Peers show up automatically on the LAN via UDP broadcast and mDNS. Tailscale nodes are picked up too. You can also punch in an IP manually if discovery misses something.

Audio is 48 kHz mono PCM at 16, 24, or 32-bit float - pick what you want under Format. Low latency matters here, so there’s an adaptive buffer on the receive side that steps up when playback underruns and steps down when things are stable. You can set the buffer yourself and lock it if auto-tuning isn’t what you want.

VU meters on send and receive so you can see levels without guessing.

## Features

- Send / Receive modes with separate device pickers
- LAN discovery (UDP + mDNS) and Tailscale peer detection
- Manual peer by IP
- 16 / 24 / 32-bit PCM format selector
- Adaptive latency buffer (5–120 ms) with manual override and lock
- Input and output level meters
- Windows and Linux (AppImage, no .NET install needed on Linux)

## Firewall

Both machines need inbound **UDP 18240** (discovery) and **TCP 18241** (audio).

## From source

Requires .NET 8 SDK.

```bash
dotnet run --project src/CrossPlatformMicStreamer
```

To build the Linux AppImage locally:

```powershell
.\packaging\build-appimage.ps1
```
