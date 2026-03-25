#!/usr/bin/env python3
"""
HamBridge Pi Server
───────────────────
Bidirectional audio bridge between a Raspberry Pi (connected to a ham radio)
and a Windows PC running the HamBridge WPF client.

Ports:
  UDP 5000  Pi → Windows   RX audio  (radio → PC)
  UDP 5001  Windows → Pi   TX audio  (PC → radio)
  TCP 5002  Windows → Pi   Keepalive (PING every 5 s)

Audio format: 48 000 Hz · 16-bit PCM · Mono · 20 ms frames
"""

import json
import logging
import os
import signal
import socket
import sys
import threading
import time
from pathlib import Path

import pyaudio
import subprocess

# ── logging ───────────────────────────────────────────────────────────────────
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-7s  %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("hambridge")

# ── constants ─────────────────────────────────────────────────────────────────
SAMPLE_RATE    = 48_000
CHANNELS       = 1
SAMPLE_WIDTH   = 2          # 16-bit = 2 bytes
FRAME_MS       = 20
FRAMES_PER_BUF = SAMPLE_RATE * FRAME_MS // 1000   # 960 frames per 20 ms chunk
BYTES_PER_BUF  = FRAMES_PER_BUF * CHANNELS * SAMPLE_WIDTH   # 1920 bytes

# ── default config ─────────────────────────────────────────────────────────────
DEFAULT_CONFIG = {
    "windows_host": "",          # set at runtime from first UDP packet or set here
    "rx_port":      5000,        # Pi sends RX audio to Windows on this port
    "tx_port":      5001,        # Pi listens for TX audio from Windows on this port
    "tcp_port":     5002,        # TCP keepalive port
    "rx_device":    None,        # ALSA device name for RX capture (None = default)
    "tx_device":    None,        # ALSA device name for TX playback (None = default)
    "camera_port":  5003,        # HTTP port for MJPEG stream
    "camera_enabled": True,      # Set False to disable camera entirely
    "camera_width":  1280,       # Capture width  (pixels)
    "camera_height":  720,       # Capture height (pixels)
    "camera_fps":      15,       # Target frame rate
}

CONFIG_PATH = Path(__file__).parent / "hambridge_pi.json"

# ─────────────────────────────────────────────────────────────────────────────

def load_config() -> dict:
    cfg = DEFAULT_CONFIG.copy()
    if CONFIG_PATH.exists():
        try:
            with open(CONFIG_PATH) as f:
                cfg.update(json.load(f))
            log.info(f"Config loaded from {CONFIG_PATH}")
        except Exception as e:
            log.warning(f"Could not read config ({e}), using defaults")
    else:
        # Write a starter config so the user can edit it
        with open(CONFIG_PATH, "w") as f:
            json.dump(DEFAULT_CONFIG, f, indent=2)
        log.info(f"Default config written to {CONFIG_PATH} — edit as needed")
    return cfg


def list_devices(pa: pyaudio.PyAudio) -> None:
    """Print all available ALSA devices (helpful for config)."""
    print("\nAvailable audio devices:")
    print(f"  {'Index':<6} {'Name':<45} {'In ch':<7} {'Out ch'}")
    print("  " + "-" * 70)
    for i in range(pa.get_device_count()):
        d = pa.get_device_info_by_index(i)
        print(f"  {i:<6} {d['name']:<45} {int(d['maxInputChannels']):<7} {int(d['maxOutputChannels'])}")
    print()


def find_device_index(pa: pyaudio.PyAudio, name: str | None, for_input: bool) -> int | None:
    """Return device index for a given name substring, or None for default."""
    if name is None:
        return None
    for i in range(pa.get_device_count()):
        d = pa.get_device_info_by_index(i)
        key = "maxInputChannels" if for_input else "maxOutputChannels"
        if name.lower() in d["name"].lower() and d[key] > 0:
            return i
    log.warning(f"Device '{name}' not found — using system default")
    return None

# ─────────────────────────────────────────────────────────────────────────────
# TX  (Windows → Pi → radio playback)
# ─────────────────────────────────────────────────────────────────────────────

class TxReceiver:
    """
    Listens on UDP 5001 for audio packets from the Windows client and plays
    them to the radio TX audio input.
    """

    def __init__(self, cfg: dict, pa: pyaudio.PyAudio):
        self._cfg  = cfg
        self._pa   = pa
        self._sock: socket.socket | None = None
        self._stream: pyaudio.Stream | None = None
        self._thread: threading.Thread | None = None
        self._running = False

    def start(self):
        dev_idx = find_device_index(self._pa, self._cfg["tx_device"], for_input=False)
        dev_name = self._cfg["tx_device"] or "default"

        try:
            self._stream = self._pa.open(
                format=self._pa.get_format_from_width(SAMPLE_WIDTH),
                channels=CHANNELS,
                rate=SAMPLE_RATE,
                output=True,
                output_device_index=dev_idx,
                frames_per_buffer=FRAMES_PER_BUF,
            )
        except Exception as e:
            log.error(f"TX failed to open audio device [{dev_name}]: {e}")
            log.error("Check 'tx_device' in hambridge_pi.json — set it to the IC-7100 device name from the device list above.")
            return

        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._sock.bind(("0.0.0.0", self._cfg["tx_port"]))
        self._sock.settimeout(1.0)

        # Log the actual device info so we know what was opened
        if dev_idx is not None:
            d = self._pa.get_device_info_by_index(dev_idx)
            log.info(f"TX  opened device [{dev_idx}] '{d['name']}'  (out channels: {int(d['maxOutputChannels'])})")
        else:
            default_idx = self._pa.get_default_output_device_info()
            log.info(f"TX  opened DEFAULT output: '{default_idx['name']}'  — "
                     f"if this is wrong, set 'tx_device' in hambridge_pi.json")

        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True, name="TxReceiver")
        self._thread.start()
        log.info(f"TX  listening on UDP {self._cfg['tx_port']}  →  device: {dev_name}")

    def _loop(self):
        consecutive_errors = 0
        while self._running:
            try:
                data, addr = self._sock.recvfrom(8192)
                if not data:
                    continue
                # Log first packet so we know UDP is arriving
                if not hasattr(self, '_first_packet_logged'):
                    self._first_packet_logged = True
                    log.info(f"TX first UDP packet received from {addr[0]}  ({len(data)} bytes)")
                try:
                    self._stream.write(data)
                    consecutive_errors = 0
                except OSError as e:
                    consecutive_errors += 1
                    if consecutive_errors <= 3:
                        log.error(f"TX stream write error: {e}")
                    if consecutive_errors == 3:
                        log.error("TX stream write: suppressing further errors")
            except socket.timeout:
                continue
            except Exception as e:
                if self._running:
                    log.error(f"TX socket error: {e}")
                break

    def stop(self):
        self._running = False
        if self._sock:
            try: self._sock.close()
            except: pass
        if self._stream:
            try:
                self._stream.stop_stream()
                self._stream.close()
            except: pass
        if self._thread:
            self._thread.join(timeout=2)
        log.info("TX stopped")


# ─────────────────────────────────────────────────────────────────────────────
# RX  (radio capture → Pi → Windows)
# ─────────────────────────────────────────────────────────────────────────────

class RxSender:
    """
    Captures audio from the radio RX output and sends UDP packets to the
    Windows client on port 5000.  The Windows host address is discovered
    automatically from the first TX packet received, or can be set in config.
    """

    def __init__(self, cfg: dict, pa: pyaudio.PyAudio, windows_host_ref: list):
        self._cfg             = cfg
        self._pa              = pa
        self._windows_host    = windows_host_ref   # mutable reference [host_str]
        self._sock: socket.socket | None = None
        self._stream: pyaudio.Stream | None = None
        self._thread: threading.Thread | None = None
        self._running = False

    def start(self):
        dev_idx = find_device_index(self._pa, self._cfg["rx_device"], for_input=True)
        dev_name = self._cfg["rx_device"] or "default"

        self._stream = self._pa.open(
            format=self._pa.get_format_from_width(SAMPLE_WIDTH),
            channels=CHANNELS,
            rate=SAMPLE_RATE,
            input=True,
            input_device_index=dev_idx,
            frames_per_buffer=FRAMES_PER_BUF,
        )

        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True, name="RxSender")
        self._thread.start()
        log.info(f"RX  capturing from device: {dev_name}  →  UDP {self._cfg['rx_port']}")

    def _loop(self):
        while self._running:
            try:
                data = self._stream.read(FRAMES_PER_BUF, exception_on_overflow=False)
                host = self._windows_host[0]
                if host:
                    self._sock.sendto(data, (host, self._cfg["rx_port"]))
            except Exception as e:
                if self._running:
                    log.error(f"RX error: {e}")
                break

    def stop(self):
        self._running = False
        if self._stream:
            try:
                self._stream.stop_stream()
                self._stream.close()
            except: pass
        if self._sock:
            try: self._sock.close()
            except: pass
        if self._thread:
            self._thread.join(timeout=2)
        log.info("RX stopped")


# ─────────────────────────────────────────────────────────────────────────────
# TCP keepalive  (Windows → Pi, PING every 5 s)
# ─────────────────────────────────────────────────────────────────────────────

class KeepaliveServer:
    """
    Accepts one TCP connection at a time from the Windows client.
    Each PING received updates the known Windows IP address so RX audio
    is always sent to the right host even after a reconnect.
    """

    def __init__(self, cfg: dict, windows_host_ref: list):
        self._cfg          = cfg
        self._windows_host = windows_host_ref
        self._server: socket.socket | None = None
        self._thread: threading.Thread | None = None
        self._running = False

    def start(self):
        self._server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._server.bind(("0.0.0.0", self._cfg["tcp_port"]))
        self._server.listen(1)
        self._server.settimeout(1.0)

        self._running = True
        self._thread = threading.Thread(target=self._loop, daemon=True, name="Keepalive")
        self._thread.start()
        log.info(f"TCP keepalive listening on port {self._cfg['tcp_port']}")

    def _loop(self):
        while self._running:
            try:
                conn, addr = self._server.accept()
            except socket.timeout:
                continue
            except Exception:
                break

            host = addr[0]
            log.info(f"Windows connected from {host}")
            self._windows_host[0] = host

            conn.settimeout(15.0)   # miss 3 PINGs → timeout
            try:
                while self._running:
                    data = conn.recv(64)
                    if not data:
                        break
                    # Optionally echo PONG back
                    # conn.sendall(b"PONG\n")
            except socket.timeout:
                log.warning("Keepalive timeout — Windows may have disconnected")
            except Exception as e:
                if self._running:
                    log.warning(f"Keepalive error: {e}")
            finally:
                try: conn.close()
                except: pass
                log.info(f"Windows {host} disconnected")

    def stop(self):
        self._running = False
        if self._server:
            try: self._server.close()
            except: pass
        if self._thread:
            self._thread.join(timeout=2)
        log.info("TCP keepalive stopped")


# ─────────────────────────────────────────────────────────────────────────────
# Main
# ─────────────────────────────────────────────────────────────────────────────


# ─────────────────────────────────────────────────────────────────────────────
# Camera — launched as a completely separate process for full isolation
# from PyAudio / ALSA (sharing DMA resources in the same process breaks TX audio)
# ─────────────────────────────────────────────────────────────────────────────

class CameraProcess:
    """
    Spawns camera_server.py as a child process so its libcamera/V4L2
    initialisation never touches this process's ALSA handles.
    """

    def __init__(self, cfg: dict):
        self._cfg  = cfg
        self._proc: subprocess.Popen | None = None

    def start(self):
        if not self._cfg.get("camera_enabled", True):
            log.info("Camera disabled in config — skipping")
            return

        script = Path(__file__).parent / "camera_server.py"
        if not script.exists():
            log.error(f"camera_server.py not found at {script}")
            return

        port = self._cfg.get("camera_port",   5003)
        w    = self._cfg.get("camera_width",  1280)
        h    = self._cfg.get("camera_height",  720)
        fps  = self._cfg.get("camera_fps",      15)

        self._proc = subprocess.Popen(
            [sys.executable, str(script),
             str(port), str(w), str(h), str(fps)],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        log.info(f"Camera process started (PID {self._proc.pid}), "
                 f"MJPEG on port {port}")

    def stop(self):
        if self._proc:
            self._proc.terminate()
            try:    self._proc.wait(timeout=3)
            except: self._proc.kill()
            self._proc = None
            log.info("Camera process stopped")


def main():
    # --list-devices flag for easy setup
    if "--list-devices" in sys.argv or "-l" in sys.argv:
        pa = pyaudio.PyAudio()
        list_devices(pa)
        pa.terminate()
        return

    cfg = load_config()

    # windows_host is shared mutable state updated by KeepaliveServer,
    # read by RxSender — using a one-element list as a mutable reference.
    windows_host = [cfg.get("windows_host", "")]
    if windows_host[0]:
        log.info(f"Windows host set from config: {windows_host[0]}")
    else:
        log.info("Windows host not configured — will auto-detect from first TCP connection")

    pa = pyaudio.PyAudio()

    # Log all devices at startup so the journal shows which device to configure
    log.info("=== Audio devices on this system ===")
    for i in range(pa.get_device_count()):
        d = pa.get_device_info_by_index(i)
        log.info(f"  [{i}] {d['name']}  in={int(d['maxInputChannels'])}  out={int(d['maxOutputChannels'])}")
    log.info(f"=== TX will use: {'device ' + str(cfg['tx_device']) if cfg['tx_device'] is not None else 'DEFAULT (index 0)'} ===")
    log.info(f"=== RX will use: {'device ' + str(cfg['rx_device']) if cfg['rx_device'] is not None else 'DEFAULT (index 0)'} ===")
    log.info("If TX goes to wrong device, set 'tx_device' in hambridge_pi.json to the device name above.")

    tx = TxReceiver(cfg, pa)
    rx = RxSender(cfg, pa, windows_host)
    kp = KeepaliveServer(cfg, windows_host)

    tx.start()
    rx.start()
    kp.start()

    cam = CameraProcess(cfg)
    cam.start()

    log.info("HamBridge Pi server running.  Press Ctrl+C to stop.")

    # Graceful shutdown on SIGTERM (systemd) or SIGINT (Ctrl+C)
    stop_event = threading.Event()

    def _shutdown(sig, frame):
        log.info(f"Signal {sig} received — shutting down…")
        stop_event.set()

    signal.signal(signal.SIGTERM, _shutdown)
    signal.signal(signal.SIGINT,  _shutdown)

    stop_event.wait()

    kp.stop()
    rx.stop()
    tx.stop()
    cam.stop()
    pa.terminate()
    log.info("HamBridge stopped.")


if __name__ == "__main__":
    main()
