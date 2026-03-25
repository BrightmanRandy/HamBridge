#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# HamBridge Pi — installer
# Run once as the 'pi' user:   bash install.sh
# ─────────────────────────────────────────────────────────────────────────────
set -e

INSTALL_DIR="$HOME/hambridge"
SERVICE_NAME="hambridge"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo ""
echo "  HamBridge Pi Installer"
echo "  ──────────────────────"
echo "  Install dir : $INSTALL_DIR"
echo ""

# ── 1. System packages ────────────────────────────────────────────────────────
echo ">>> Installing system packages..."
sudo apt-get update -qq
sudo apt-get install -y \
    python3 python3-venv python3-pip \
    portaudio19-dev python3-pyaudio \
    alsa-utils \
    libcamera-apps

# ── 2. Create install directory ───────────────────────────────────────────────
echo ">>> Setting up $INSTALL_DIR ..."
mkdir -p "$INSTALL_DIR"
cp "$SCRIPT_DIR/pi_server.py"      "$INSTALL_DIR/"
cp "$SCRIPT_DIR/camera_server.py"  "$INSTALL_DIR/"
cp "$SCRIPT_DIR/hambridge_pi.json" "$INSTALL_DIR/"

# ── 3. Python venv + dependencies ─────────────────────────────────────────────
echo ">>> Creating Python virtual environment..."
python3 -m venv "$INSTALL_DIR/venv"
"$INSTALL_DIR/venv/bin/pip" install --upgrade pip -q
"$INSTALL_DIR/venv/bin/pip" install pyaudio -q

# ── 4. List audio devices so the user can configure them ─────────────────────
echo ""
echo ">>> Available audio devices:"
"$INSTALL_DIR/venv/bin/python" "$INSTALL_DIR/pi_server.py" --list-devices || true

# ── 5. Install & enable systemd service ──────────────────────────────────────
echo ">>> Installing systemd service..."
# Install as a template service instance for the current user
sudo cp "$SCRIPT_DIR/hambridge.service" /etc/systemd/system/hambridge@.service
sudo systemctl daemon-reload
sudo systemctl enable "hambridge@${USER}"

echo ""
echo "  ✓ Installation complete."
echo ""
echo "  NEXT STEPS:"
echo "  1. Edit  $INSTALL_DIR/hambridge_pi.json"
echo "     • Set 'rx_device' to your radio's capture device name (or leave null for default)"
echo "     • Set 'tx_device' to your radio's playback device name (or leave null for default)"
echo "     • Optionally hard-code 'windows_host' to your PC's IP"
echo ""
echo "  2. Start the service:"
echo "       sudo systemctl start hambridge@${USER}"
echo ""
echo "  3. Check logs:"
echo "       sudo journalctl -u hambridge@${USER} -f"
echo ""
