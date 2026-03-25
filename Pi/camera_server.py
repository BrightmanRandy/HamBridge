#!/usr/bin/env python3
"""
HamBridge Camera Server — runs as a standalone child process.
Spawned by pi_server.py so it is fully isolated from PyAudio/ALSA.

Usage (automatic — called by pi_server.py):
    python camera_server.py <port> <width> <height> <fps>
"""

import http.server
import io
import logging
import os
import shutil
import signal
import socketserver
import subprocess
import sys
import threading
import time

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  CAMERA   %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("camera")

BOUNDARY = b"frame"

# ── shared latest frame ────────────────────────────────────────────────────────
_lock         = threading.Lock()
_latest_jpeg  = b""
_running      = True


def set_frame(jpeg: bytes):
    global _latest_jpeg
    with _lock:
        _latest_jpeg = jpeg


def get_frame() -> bytes:
    with _lock:
        return _latest_jpeg


# ── capture thread ─────────────────────────────────────────────────────────────
def capture_loop(w: int, h: int, fps: int):
    global _running

    candidates = ["rpicam-vid", "libcamera-vid", "raspivid"]
    templates = {
        "rpicam-vid": [
            "rpicam-vid",
            "--width", str(w), "--height", str(h),
            "--framerate", str(fps),
            "--codec", "mjpeg", "--inline",
            "--nopreview", "--timeout", "0", "-o", "-",
        ],
        "libcamera-vid": [
            "libcamera-vid",
            "--width", str(w), "--height", str(h),
            "--framerate", str(fps),
            "--codec", "mjpeg", "--inline",
            "--nopreview", "--timeout", "0", "-o", "-",
        ],
        "raspivid": [
            "raspivid",
            "-w", str(w), "-h", str(h),
            "-fps", str(fps), "-cd", "MJPEG", "-t", "0", "-o", "-",
        ],
    }

    cmd = None
    for binary in candidates:
        if shutil.which(binary):
            cmd = templates[binary]
            log.info(f"Using {binary}")
            break

    if cmd is None:
        log.error("No camera binary found. Run: sudo apt install libcamera-apps")
        return

    SOI = b"\xff\xd8"
    EOI = b"\xff\xd9"

    while _running:
        proc = None
        try:
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                bufsize=0,
            )
            log.info("Capture started")
            buf = bytearray()
            while _running:
                chunk = proc.stdout.read(65536)
                if not chunk:
                    break
                buf.extend(chunk)
                while True:
                    start = buf.find(SOI)
                    if start == -1:
                        buf.clear()
                        break
                    end = buf.find(EOI, start + 2)
                    if end == -1:
                        del buf[:start]
                        break
                    end += 2
                    set_frame(bytes(buf[start:end]))
                    del buf[:end]
        except Exception as e:
            if _running:
                log.error(f"Capture error: {e}. Retrying in 5s...")
                time.sleep(5)
        finally:
            if proc:
                try:    proc.wait(timeout=2)
                except: proc.kill()


# ── HTTP MJPEG server ──────────────────────────────────────────────────────────
class MjpegHandler(http.server.BaseHTTPRequestHandler):
    fps = 15

    def log_message(self, fmt, *args):
        pass  # silence per-request logs

    def do_GET(self):
        if self.path not in ("/stream", "/"):
            self.send_error(404)
            return

        self.send_response(200)
        self.send_header("Content-Type",
                         f"multipart/x-mixed-replace; boundary={BOUNDARY.decode()}")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection",    "close")
        self.end_headers()

        delay = 1.0 / max(self.fps, 1)
        last  = b""
        try:
            while _running:
                frame = get_frame()
                if not frame or frame is last:
                    time.sleep(0.01)
                    continue
                last = frame
                header = (
                    "--" + BOUNDARY.decode() + "\r\n"
                    "Content-Type: image/jpeg\r\n"
                    "Content-Length: " + str(len(frame)) + "\r\n\r\n"
                ).encode()
                self.wfile.write(header + frame + b"\r\n")
                self.wfile.flush()
                time.sleep(delay)
        except (BrokenPipeError, ConnectionResetError):
            pass


class ReusableTCPServer(socketserver.TCPServer):
    allow_reuse_address = True


def main():
    global _running

    if len(sys.argv) < 5:
        print("Usage: camera_server.py <port> <width> <height> <fps>")
        sys.exit(1)

    port = int(sys.argv[1])
    w    = int(sys.argv[2])
    h    = int(sys.argv[3])
    fps  = int(sys.argv[4])

    MjpegHandler.fps = fps

    def _shutdown(sig, frame):
        global _running
        _running = False
        os._exit(0)

    signal.signal(signal.SIGTERM, _shutdown)
    signal.signal(signal.SIGINT,  _shutdown)

    # Start capture in background thread
    t = threading.Thread(target=capture_loop, args=(w, h, fps), daemon=True)
    t.start()

    # Serve HTTP — blocks until process is killed
    log.info(f"MJPEG server on port {port}  ({w}x{h} @ {fps} fps)")
    with ReusableTCPServer(("0.0.0.0", port), MjpegHandler) as srv:
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
