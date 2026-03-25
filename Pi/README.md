# HamBridge — Raspberry Pi Setup

Bidirectional audio bridge between your Pi (connected to your radio) and the
Windows HamBridge WPF client on your PC.

---

## How it works

```
Radio RX out ──► Pi capture ──► UDP 5000 ──► Windows speakers / WSJT-X
Radio TX in  ◄── Pi playback ◄── UDP 5001 ◄── Windows mic / WSJT-X
                                 TCP 5002      Keepalive (auto-discovers PC IP)
```

- Audio is streamed **continuously** — no PTT involved on the software side.
- The Pi auto-discovers your PC's IP from the first TCP keepalive connection,
  so you don't need to hard-code it (though you can).

---

## Requirements

| Item | Notes |
|---|---|
| Raspberry Pi 3B+ or newer | Pi 4 / Pi 5 recommended for headroom |
| Raspberry Pi OS (Bookworm or Bullseye) | 64-bit or 32-bit both fine |
| USB audio interface | Connected to radio's audio in/out |
| Network | Pi and PC on the same LAN (wired recommended) |

---

## 1 — Transfer files to the Pi

From your Windows PC, copy the three files to the Pi.  The easiest way is
**WinSCP** (free) or `scp` from PowerShell:

```powershell
scp pi_server.py hambridge_pi.json hambridge.service install.sh pi@raspberrypi.local:~
```

Or use WinSCP and drop them anywhere in `/home/pi`.

---

## 2 — Run the installer

SSH into the Pi:

```bash
ssh pi@raspberrypi.local
```

Make the installer executable and run it:

```bash
chmod +x install.sh
bash install.sh
```

The installer will:
- Install `portaudio` and `pyaudio` system packages
- Create `~/hambridge/` with a Python virtualenv
- Print all available audio devices (important for the next step)
- Install and enable the `hambridge` systemd service

---

## 3 — Identify your audio devices

The installer prints a device table like this:

```
Available audio devices:
  Index  Name                                          In ch   Out ch
  ──────────────────────────────────────────────────────────────────
  0      bcm2835 Headphones                            0       2
  1      USB Audio Device                              2       2
  2      USB Audio Device                              0       2
```

Your USB audio interface connected to the radio will appear here.
Note the **name** of the device (e.g. `USB Audio Device`).

You can re-run this any time:

```bash
~/hambridge/venv/bin/python ~/hambridge/pi_server.py --list-devices
```

---

## 4 — Edit the config

```bash
nano ~/hambridge/hambridge_pi.json
```

```json
{
  "windows_host": "",
  "rx_port": 5000,
  "tx_port": 5001,
  "tcp_port": 5002,
  "rx_device": "USB Audio Device",
  "tx_device": "USB Audio Device"
}
```

| Field | What to set |
|---|---|
| `windows_host` | Leave `""` to auto-detect from TCP, or set to your PC's IP e.g. `"192.168.1.50"` |
| `rx_device` | Name substring of the capture device (radio RX out → Pi in). `null` = system default |
| `tx_device` | Name substring of the playback device (Pi out → radio TX in). `null` = system default |
| `rx_port` | Must match Windows client RX Port setting (default 5000) |
| `tx_port` | Must match Windows client TX Port setting (default 5001) |
| `tcp_port` | Must match Windows client TCP Port setting (default 5002) |

> **Tip:** If your radio interface shows up as two separate entries (one for
> input, one for output), you can set `rx_device` and `tx_device` to different
> name substrings.

---

## 5 — Start the service

```bash
sudo systemctl start hambridge
```

Check it started cleanly:

```bash
sudo journalctl -u hambridge -f
```

You should see:

```
HH:MM:SS  INFO     Config loaded from /home/pi/hambridge/hambridge_pi.json
HH:MM:SS  INFO     TX  listening on UDP 5001  →  device: USB Audio Device
HH:MM:SS  INFO     RX  capturing from device: USB Audio Device  →  UDP 5000
HH:MM:SS  INFO     TCP keepalive listening on port 5002
HH:MM:SS  INFO     HamBridge Pi server running.  Press Ctrl+C to stop.
```

When you connect from Windows:

```
HH:MM:SS  INFO     Windows connected from 192.168.1.50
```

---

## 6 — Firewall (if applicable)

If you have `ufw` enabled on the Pi, allow the three ports:

```bash
sudo ufw allow 5000/udp
sudo ufw allow 5001/udp
sudo ufw allow 5002/tcp
```

---

## Service management cheat-sheet

```bash
sudo systemctl start   hambridge    # start now
sudo systemctl stop    hambridge    # stop now
sudo systemctl restart hambridge    # restart
sudo systemctl status  hambridge    # quick status
sudo journalctl -u hambridge -f     # live logs
sudo journalctl -u hambridge -n 50  # last 50 lines
```

The service is set to **start automatically on boot**.  To disable autostart:

```bash
sudo systemctl disable hambridge
```

---

## Troubleshooting

### No audio devices listed / PortAudio error
```bash
# Check ALSA sees your USB interface
aplay -l    # playback devices
arecord -l  # capture devices
```
If the USB interface isn't listed, unplug and replug it, then try again.

### RX audio choppy or has dropouts
- Switch from Wi-Fi to wired Ethernet — UDP audio is sensitive to jitter
- Lower `FRAME_MS` in `pi_server.py` from 20 to 10 (rebuild/restart after)
- Check CPU usage: `htop`

### "Windows connected" never appears in logs
- Confirm the PC and Pi are on the same network: `ping raspberrypi.local` from PowerShell
- Check the Pi hostname: `hostname` — if not `raspberrypi`, update the Pi Host field in HamBridge Settings
- Or use the Pi's IP address directly instead of its hostname

### TX audio not reaching the radio
- Confirm `tx_device` in the config matches the playback side of your USB interface
- Check radio audio input levels with `alsamixer` on the Pi

### Permission denied on audio device
```bash
# Add pi user to the audio group
sudo usermod -aG audio pi
# Then reboot
sudo reboot
```

---

## ALSA device config (optional — for persistent device naming)

If you have multiple USB audio devices and they swap indices on reboot,
create `/etc/asound.conf` to pin them by USB port:

```
# /etc/asound.conf
pcm.radio {
    type hw
    card "Device"    # match the card name from 'aplay -l'
}
ctl.radio {
    type hw
    card "Device"
}
```

Then set `"rx_device": "radio"` and `"tx_device": "radio"` in the config.
