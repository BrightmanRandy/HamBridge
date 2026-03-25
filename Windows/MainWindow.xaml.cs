using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HamBridgeWpf
{
    public partial class MainWindow : Window
    {
        private AppConfig         _config;
        private AudioEngine?      _engine;
        private DispatcherTimer?  _meterTimer;
        private CameraClient?     _camera;
        private int               _cameraFrameCount;
        private DateTime          _cameraFpsTime = DateTime.UtcNow;

        // ── ctor ─────────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            _config = AppConfig.Load();
            LoadUi();
            PopulateDevices();
            StartMeterTimer();
        }

        // ── UI initialisation ─────────────────────────────────────────────────────
        private void LoadUi()
        {
            PiHostBox.Text             = _config.PiHost;
            RxPortBox.Text             = _config.RxPort.ToString();
            TxPortBox.Text             = _config.TxPort.ToString();
            DigitalModeCheck.IsChecked = _config.DigitalMode;
            CameraPortBox.Text         = _config.CameraPort.ToString();
            UpdateModeText();
        }

        private void PopulateDevices()
        {
            // Build WaveIn list (microphone-class devices)
            var waveIn = new List<(int idx, string name)>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
                waveIn.Add((i, WaveIn.GetCapabilities(i).ProductName));

            // Build WaveOut list (playback devices)
            var waveOut = new List<(int idx, string name)> { (-1, "(None / Disabled)") };
            for (int i = 0; i < WaveOut.DeviceCount; i++)
                waveOut.Add((i, WaveOut.GetCapabilities(i).ProductName));

            FillCombo(MicDeviceCombo,       waveIn,  _config.MicDeviceIdx);
            FillCombo(TxDigitalDeviceCombo, waveIn,  _config.TxDigitalDeviceIdx);
            FillCombo(RxDeviceCombo,        waveOut, _config.RxDeviceIdx);
            FillCombo(RxMirrorDeviceCombo,  waveOut, _config.RxMirrorDeviceIdx);
        }

        private static void FillCombo(ComboBox combo,
                                      List<(int idx, string name)> items,
                                      int selectedIdx)
        {
            combo.Items.Clear();
            int selectAt = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var (idx, name) = items[i];
                combo.Items.Add(new ComboBoxItem { Content = name, Tag = idx });
                if (idx == selectedIdx) selectAt = i;
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = selectAt;
        }

        private int GetComboIdx(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: int idx })
                return idx;
            return -1;
        }

        // ── Radio tab ─────────────────────────────────────────────────────────────
        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            // Snapshot current UI into config (don't require explicit Save first)
            ReadUiIntoConfig();

            _engine?.Dispose();
            _engine = new AudioEngine(_config);
            _engine.LogMessage        += msg       => Dispatcher.Invoke(() => AppendLog(msg));
            _engine.ConnectionChanged += connected => Dispatcher.Invoke(() => OnConnectionChanged(connected));
            _engine.MicVolume = (float)(MicVolumeSlider.Value / 100.0);
            _engine.RxVolume  = (float)(RxVolumeSlider.Value  / 100.0);
            _engine.Start();

            ConnectBtn.IsEnabled    = false;
            DisconnectBtn.IsEnabled = true;
            AppendLog($"Starting — host: {_config.PiHost}  mode: {(_config.DigitalMode ? "Digital" : "Normal")}");
        }

        private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            _engine?.Dispose();
            _engine = null;
            ConnectBtn.IsEnabled    = true;
            DisconnectBtn.IsEnabled = false;
            OnConnectionChanged(false);
            AppendLog("Disconnected.");
        }

        private void DigitalModeCheck_Changed(object sender, RoutedEventArgs e)
        {
            _config.DigitalMode = DigitalModeCheck.IsChecked == true;
            UpdateModeText();
        }

        // ── Settings tab ──────────────────────────────────────────────────────────
        private void SaveSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            ReadUiIntoConfig();
            _config.Save();
            AppendLog("Settings saved.");
        }

        private void ReadUiIntoConfig()
        {
            _config.PiHost = PiHostBox.Text.Trim();

            if (int.TryParse(RxPortBox.Text, out int rp)) _config.RxPort = rp;
            if (int.TryParse(TxPortBox.Text, out int tp)) _config.TxPort = tp;

            _config.MicDeviceIdx       = GetComboIdx(MicDeviceCombo);
            _config.RxDeviceIdx        = GetComboIdx(RxDeviceCombo);
            _config.RxMirrorDeviceIdx  = GetComboIdx(RxMirrorDeviceCombo);
            _config.TxDigitalDeviceIdx = GetComboIdx(TxDigitalDeviceCombo);
            _config.DigitalMode        = DigitalModeCheck.IsChecked == true;
            if (int.TryParse(CameraPortBox.Text, out int cp)) _config.CameraPort = cp;
        }

        // ── Log tab ───────────────────────────────────────────────────────────────
        private void ClearLogBtn_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

        // ── Helpers ───────────────────────────────────────────────────────────────
        private void OnConnectionChanged(bool connected)
        {
            StatusText.Text = connected ? "Connected" : "Disconnected";
            StatusText.Foreground = connected
                ? new SolidColorBrush(Color.FromRgb(0x6B, 0xFF, 0x6B))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        }

        private void UpdateModeText() =>
            ModeText.Text = $"Mode: {(_config.DigitalMode ? "Digital" : "Normal")}";

        private void AppendLog(string msg)
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            LogBox.ScrollToEnd();
        }

        // ── Volume sliders ────────────────────────────────────────────────────────
        private void MicVolumeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int pct = (int)e.NewValue;
            if (MicVolumeLabel != null)
                MicVolumeLabel.Text = $"{pct}%";
            if (_engine != null)
                _engine.MicVolume = pct / 100f;
        }

        private void RxVolumeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int pct = (int)e.NewValue;
            if (RxVolumeLabel != null)
                RxVolumeLabel.Text = $"{pct}%";
            if (_engine != null)
                _engine.RxVolume = pct / 100f;
        }

        // ── Camera ───────────────────────────────────────────────────────────────
        private void CameraConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            ReadUiIntoConfig();
            StartCamera();
        }

        private void CameraDisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            _camera?.Stop();
            _camera = null;
            CameraConnectBtn.IsEnabled    = true;
            CameraDisconnectBtn.IsEnabled = false;
            CameraStatusText.Text         = "Disconnected";
            CameraFpsText.Text            = "";
            CameraImage.Source            = null;
        }

        private void SnapshotBtn_Click(object sender, RoutedEventArgs e)
        {
            if (CameraImage.Source is not System.Windows.Media.Imaging.BitmapImage bmp) return;
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title      = "Save Snapshot",
                    Filter     = "JPEG Image|*.jpg",
                    FileName   = $"hambridge_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
                };
                if (dlg.ShowDialog() != true) return;
                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = System.IO.File.OpenWrite(dlg.FileName);
                encoder.Save(fs);
                AppendLog($"Snapshot saved: {dlg.FileName}");
            }
            catch (Exception ex) { AppendLog($"Snapshot error: {ex.Message}"); }
        }

        private void StartCamera()
        {
            _camera?.Stop();
            _camera = new CameraClient(_config);
            _camera.FrameReady     += OnCameraFrame;
            _camera.StatusChanged  += s => Dispatcher.Invoke(() =>
            {
                CameraStatusText.Text         = s;
                bool live = s == "Streaming";
                CameraConnectBtn.IsEnabled    = !live;
                CameraDisconnectBtn.IsEnabled = live;
            });
            _camera.LogMessage     += msg => Dispatcher.Invoke(() => AppendLog(msg));
            _camera.Start();
        }

        private void OnCameraFrame(System.Windows.Media.Imaging.BitmapImage frame)
        {
            Dispatcher.Invoke(() =>
            {
                CameraImage.Source = frame;

                // FPS counter
                _cameraFrameCount++;
                var now = DateTime.UtcNow;
                double elapsed = (now - _cameraFpsTime).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    CameraFpsText.Text    = $"{_cameraFrameCount / elapsed:F1} fps";
                    _cameraFrameCount     = 0;
                    _cameraFpsTime        = now;
                }
            });
        }

        // ── Meter timer ───────────────────────────────────────────────────────────
        private void StartMeterTimer()
        {
            _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _meterTimer.Tick += OnMeterTick;
            _meterTimer.Start();
        }

        private void OnMeterTick(object? sender, EventArgs e)
        {
            if (_engine == null)
            {
                // Engine stopped — zero out meters
                SetMeter(MicMeter, MicMeterLabel, 0f, 0f);
                SetMeter(RxMeter,  RxMeterLabel,  0f, 0f);
                return;
            }

            _engine.TickPeakDecay();
            SetMeter(MicMeter, MicMeterLabel, _engine.MicLevel, _engine.MicPeak);
            SetMeter(RxMeter,  RxMeterLabel,  _engine.RxLevel,  _engine.RxPeak);
        }

        /// <summary>
        /// Updates a meter ProgressBar and its dB label.
        /// Positions the peak-tick rectangle if present.
        /// </summary>
        private static void SetMeter(ProgressBar bar, TextBlock label, float rms, float peak)
        {
            // Convert linear RMS → dB, map to 0–100 display range (-60 dB floor)
            const float floorDb = -60f;
            float db = rms > 0 ? 20f * MathF.Log10(rms) : floorDb;
            db = Math.Max(db, floorDb);
            double pct = (db - floorDb) / (-floorDb) * 100.0;   // 0–100

            bar.Value = pct;
            label.Text = db <= floorDb ? "–∞" : $"{db:F0}";

            // Move the peak tick inside the template
            if (bar.Template.FindName("PeakTick", bar) is System.Windows.Shapes.Rectangle tick)
            {
                float peakDb  = peak > 0 ? 20f * MathF.Log10(peak) : floorDb;
                peakDb = Math.Max(peakDb, floorDb);
                double peakPct = (peakDb - floorDb) / (-floorDb);
                double barWidth = bar.ActualWidth > 4 ? bar.ActualWidth - 2 : 0;
                tick.Margin = new Thickness(peakPct * barWidth, 0, 0, 0);
            }
        }

        // ── Window closing ────────────────────────────────────────────────────────
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _meterTimer?.Stop();
            _camera?.Stop();
            _engine?.Dispose();
            _config.Save();
        }
    }
}
