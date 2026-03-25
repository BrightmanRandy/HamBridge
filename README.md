# HamBridge

Bidirectional audio bridge between a Windows PC and a Raspberry Pi connected to a ham radio, with CSI camera monitoring.

## Architecture

```
Radio RX out ──► Pi capture ──► UDP 5000 ──► Windows speakers / WSJT-X decode
Radio TX in  ◄── Pi playback ◄── UDP 5001 ◄── Windows mic / WSJT-X transmit
                                 TCP 5002      Keepalive / connection management
Pi Camera    ──────────────────► HTTP 5003 ──► Windows Camera tab (MJPEG)
```

- **No PTT integration** — radio keyed externally via Ham Radio Deluxe
- **Digital mode support** — WSJT-X / JTDX via VB-CABLE virtual audio
- **VU meters** on TX and RX audio paths
- **Live camera feed** from Pi CSI camera to monitor radio front panel

## Repository Structure

```
Windows/    WPF .NET 8 client application
Pi/         Raspberry Pi Python server
```

## Quick Start

### Windows
1. Install [.NET 8 SDK](https://dotnet.microsoft.com/download)
2. Run `install_vbaudio.ps1` as Administrator (installs VB-CABLE)
3. Run `build.bat` → produces `publish\HamBridge.exe`

### Raspberry Pi
1. Copy the `Pi/` folder to the Pi
2. `bash install.sh`
3. Edit `~/hambridge/hambridge_pi.json` — set `rx_device` and `tx_device` to your radio's USB audio device name
4. `sudo systemctl start hambridge@<yourusername>`

See `Pi/README.md` for full setup instructions.

## Requirements

| Component | Requirement |
|---|---|
| Windows PC | Windows 10/11 x64, .NET 8 SDK |
| Raspberry Pi | Pi 3B+ or newer, Pi OS Bookworm |
| Radio interface | USB audio device (tested with ICOM IC-7100) |
| Virtual audio | VB-CABLE (installer included) |

## Callsign

W4HAM
