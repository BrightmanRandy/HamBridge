using System;
using System.IO;
using System.Text.Json;

namespace HamBridgeWpf
{
    public class AppConfig
    {
        // ── persisted path ────────────────────────────────────────────────────────
        private static readonly string ConfigPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HamBridge",
                "hambridge.json");   // Old hamradio_bridge.json should be deleted manually

        // ── network ───────────────────────────────────────────────────────────────
        public string PiHost  { get; set; } = "raspberrypi.local";
        public int    RxPort  { get; set; } = 5000;   // Pi → Windows
        public int    TxPort  { get; set; } = 5001;   // Windows → Pi
        public int    TcpPort { get; set; } = 5002;   // keepalive

        // ── audio devices (WaveIn/WaveOut indices, -1 = none/default) ─────────────
        /// <summary>Microphone used in normal voice mode (TX).</summary>
        public int MicDeviceIdx { get; set; } = 0;

        /// <summary>Speaker/headphone output for received audio.</summary>
        public int RxDeviceIdx { get; set; } = 0;

        /// <summary>
        /// WaveOut device to mirror RX audio into so WSJT-X/JTDX can receive it.
        /// Typically "CABLE Input" (VB-CABLE).  -1 = disabled.
        /// </summary>
        public int RxMirrorDeviceIdx { get; set; } = -1;

        /// <summary>
        /// WaveIn device for digital-mode TX audio.
        /// Typically "CABLE Output" — WSJT-X/JTDX sends audio to "CABLE Input",
        /// Windows reads it back via "CABLE Output".
        /// (Renamed from TxLoopbackDeviceIdx.)
        /// </summary>
        public int TxDigitalDeviceIdx { get; set; } = 0;

        // ── camera ───────────────────────────────────────────────────────────────
        /// <summary>HTTP port serving the MJPEG stream from the Pi camera.</summary>
        public int  CameraPort    { get; set; } = 5003;
        /// <summary>True = show camera tab and auto-connect on start.</summary>
        public bool CameraEnabled { get; set; } = true;

        // ── mode ──────────────────────────────────────────────────────────────────
        /// <summary>False = mic → Pi;  True = CABLE Output → Pi.</summary>
        public bool DigitalMode { get; set; } = false;

        // ── persistence ───────────────────────────────────────────────────────────
        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg  = JsonSerializer.Deserialize<AppConfig>(json);
                    return cfg ?? new AppConfig();
                }
            }
            catch { /* fall through to defaults */ }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, opts));
            }
            catch { /* best-effort */ }
        }
    }
}
