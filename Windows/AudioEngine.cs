using NAudio.Wave;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace HamBridgeWpf
{
    /// <summary>
    /// Manages bidirectional audio between the Windows PC and the Pi.
    ///
    ///  TX (Win → Pi, UDP 5001):
    ///    Normal mode  — WaveInEvent on MicDeviceIdx
    ///    Digital mode — WaveInEvent on TxDigitalDeviceIdx ("CABLE Output")
    ///    No WASAPI loopback; plain WaveIn avoids COM device-change storms.
    ///
    ///  RX (Pi → Win, UDP 5000):
    ///    Primary playback  → WaveOutEvent on RxDeviceIdx (speakers/headphones)
    ///    Mirror (optional) → WaveOutEvent on RxMirrorDeviceIdx ("CABLE Input")
    ///    so WSJT-X / JTDX can decode incoming audio via "CABLE Output".
    ///
    ///  Keepalive — TCP 5002, PING every 5 s, exponential-backoff reconnect.
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        // ── audio format ──────────────────────────────────────────────────────────
        private const int SampleRate    = 48_000;
        private const int Channels      = 1;
        private const int BitsPerSample = 16;
        private const int BufferMs      = 20;       // capture granularity

        // ── state ─────────────────────────────────────────────────────────────────
        private readonly AppConfig _cfg;
        private volatile bool _running;

        // ── volume (0.0 – 1.0) ───────────────────────────────────────────────────
        private volatile float _micVolume = 1.0f;
        private volatile float _rxVolume  = 1.0f;

        // ── levels (0.0 – 1.0 RMS, with peak hold) ──────────────────────────────
        private volatile float _micLevel  = 0f;
        private volatile float _rxLevel   = 0f;
        private volatile float _micPeak   = 0f;
        private volatile float _rxPeak    = 0f;
        private const    float PeakDecay  = 0.92f;   // multiplied each ~30 ms timer tick

        /// <summary>Current TX RMS level 0–1, updated per audio buffer.</summary>
        public float MicLevel => _micLevel;
        /// <summary>Current RX RMS level 0–1, updated per audio packet.</summary>
        public float RxLevel  => _rxLevel;
        /// <summary>Peak-hold TX level 0–1 (decays between calls to TickPeakDecay).</summary>
        public float MicPeak  => _micPeak;
        /// <summary>Peak-hold RX level 0–1.</summary>
        public float RxPeak   => _rxPeak;

        /// <summary>
        /// Call on a UI timer (~30 ms) to decay the peak indicators.
        /// </summary>
        public void TickPeakDecay()
        {
            if (_micPeak > 0) _micPeak *= PeakDecay;
            if (_rxPeak  > 0) _rxPeak  *= PeakDecay;
        }

        /// <summary>TX microphone gain  (0.0 – 1.0).  Applied in software to PCM samples.</summary>
        public float MicVolume
        {
            get => _micVolume;
            set => _micVolume = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>RX speaker volume  (0.0 – 1.0).  Applied directly to WaveOut.</summary>
        public float RxVolume
        {
            get => _rxVolume;
            set
            {
                _rxVolume = Math.Clamp(value, 0f, 1f);
                if (_rxOut       != null) _rxOut.Volume       = _rxVolume;
                if (_rxMirrorOut != null) _rxMirrorOut.Volume = _rxVolume;
            }
        }

        // TX
        private WaveInEvent? _txWaveIn;
        private UdpClient?   _txUdp;
        private IPEndPoint?  _piEndPoint;   // resolved once at start — avoids per-packet DNS

        // RX
        private UdpClient?            _rxUdp;
        private Thread?               _rxThread;
        private WaveOutEvent?         _rxOut;
        private BufferedWaveProvider? _rxBuf;
        private WaveOutEvent?         _rxMirrorOut;
        private BufferedWaveProvider? _rxMirrorBuf;

        // TCP keepalive
        private Thread? _tcpThread;

        // ── events ────────────────────────────────────────────────────────────────
        public event Action<string>? LogMessage;
        public event Action<bool>?   ConnectionChanged;

        // ─────────────────────────────────────────────────────────────────────────
        public AudioEngine(AppConfig cfg) => _cfg = cfg;

        // ── lifecycle ─────────────────────────────────────────────────────────────
        public void Start()
        {
            if (_running) return;
            _running = true;
            StartTx();
            StartRx();
            StartTcpKeepalive();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            // Order: stop capture first, then playback, then sockets
            StopTx();
            StopRx();
            StopTcp();
        }

        public void Dispose() => Stop();

        // ── TX ────────────────────────────────────────────────────────────────────
        private void StartTx()
        {
            int devIdx   = _cfg.DigitalMode ? _cfg.TxDigitalDeviceIdx : _cfg.MicDeviceIdx;
            string label = _cfg.DigitalMode ? "Digital (CABLE Output)" : "Mic";

            // Resolve hostname → IP once here so OnTxData never does DNS per packet.
            // Per-packet DNS on mDNS (.local) hosts silently drops packets under load.
            try
            {
                var addresses = System.Net.Dns.GetHostAddresses(_cfg.PiHost);
                if (addresses.Length == 0)
                    throw new Exception("No addresses returned");
                _piEndPoint = new IPEndPoint(addresses[0], _cfg.TxPort);
                Log($"TX resolved {_cfg.PiHost} → {addresses[0]}");
            }
            catch (Exception ex)
            {
                Log($"TX DNS resolution failed for '{_cfg.PiHost}': {ex.Message}");
                _piEndPoint = null;
            }

            _txUdp = new UdpClient();

            _txWaveIn = new WaveInEvent
            {
                DeviceNumber      = devIdx,
                WaveFormat        = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = BufferMs
            };
            _txWaveIn.DataAvailable    += OnTxData;
            _txWaveIn.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                    Log($"TX recording stopped: {e.Exception.Message}");
            };

            try
            {
                _txWaveIn.StartRecording();
                Log($"TX started [{label}, device {devIdx}]");
            }
            catch (Exception ex)
            {
                Log($"TX failed to start: {ex.Message}");
            }
        }

        private int _txPacketCount = 0;   // for first-packet confirmation log

        private void OnTxData(object? sender, WaveInEventArgs e)
        {
            // Capture locals atomically — StopTx() can null these on another thread
            var udp = _txUdp;
            var ep  = _piEndPoint;
            if (!_running || udp == null || ep == null || e.BytesRecorded == 0) return;

            try
            {
                // Copy buffer to avoid NAudio reuse race; apply mic gain
                var packet = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, packet, 0, e.BytesRecorded);

                float gain = _micVolume;
                if (gain < 0.999f)
                {
                    for (int i = 0; i + 1 < packet.Length; i += 2)
                    {
                        short sample = (short)(packet[i] | (packet[i + 1] << 8));
                        sample = (short)Math.Clamp(sample * gain, short.MinValue, short.MaxValue);
                        packet[i]     = (byte)(sample & 0xFF);
                        packet[i + 1] = (byte)((sample >> 8) & 0xFF);
                    }
                }

                // Compute RMS for the TX meter
                double sumSq = 0;
                for (int i = 0; i + 1 < packet.Length; i += 2)
                {
                    short s = (short)(packet[i] | (packet[i + 1] << 8));
                    sumSq += (double)s * s;
                }
                float rms = (float)Math.Sqrt(sumSq / (packet.Length / 2)) / 32768f;
                _micLevel = rms;
                if (rms > _micPeak) _micPeak = rms;

                // Send — use pre-resolved IPEndPoint, never DNS per packet
                udp.Send(packet, packet.Length, ep);

                // Log first packet so we know the path is alive
                if (++_txPacketCount == 1)
                    Log($"TX first packet sent to {ep}  ({packet.Length} bytes)");
            }
            catch (Exception ex)
            {
                // Log send errors — previously swallowed silently
                Log($"TX send error: {ex.Message}");
            }
        }

        private void StopTx()
        {
            try { _txWaveIn?.StopRecording(); } catch { }
            _txWaveIn?.Dispose();
            _txWaveIn = null;

            try { _txUdp?.Close(); } catch { }
            _txUdp      = null;
            _piEndPoint = null;
            _txPacketCount = 0;
        }

        // ── RX ────────────────────────────────────────────────────────────────────
        private void StartRx()
        {
            var fmt = new WaveFormat(SampleRate, BitsPerSample, Channels);

            // Primary speaker output
            _rxBuf = new BufferedWaveProvider(fmt) { DiscardOnBufferOverflow = true };
            _rxOut = new WaveOutEvent { DeviceNumber = _cfg.RxDeviceIdx, DesiredLatency = 100 };
            _rxOut.Init(_rxBuf);
            _rxOut.Volume = _rxVolume;
            _rxOut.Play();

            // Optional mirror → VB-CABLE Input so WSJT-X/JTDX can read "CABLE Output"
            if (_cfg.RxMirrorDeviceIdx >= 0)
            {
                _rxMirrorBuf = new BufferedWaveProvider(fmt) { DiscardOnBufferOverflow = true };
                _rxMirrorOut = new WaveOutEvent
                {
                    DeviceNumber  = _cfg.RxMirrorDeviceIdx,
                    DesiredLatency = 100
                };
                _rxMirrorOut.Init(_rxMirrorBuf);
                _rxMirrorOut.Volume = _rxVolume;
                _rxMirrorOut.Play();
            }

            // Bind receive socket
            _rxUdp = new UdpClient();
            _rxUdp.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ExclusiveAddressUse,
                true);
            _rxUdp.Client.Bind(new IPEndPoint(IPAddress.Any, _cfg.RxPort));

            _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "RxAudio" };
            _rxThread.Start();
            Log($"RX listening on UDP {_cfg.RxPort}");
        }

        private void RxLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] data = _rxUdp!.Receive(ref ep);
                    _rxBuf?.AddSamples(data, 0, data.Length);
                    _rxMirrorBuf?.AddSamples(data, 0, data.Length);

                    // Compute RMS level for the meter
                    double rSumSq = 0;
                    for (int i = 0; i + 1 < data.Length; i += 2)
                    {
                        short s = (short)(data[i] | (data[i + 1] << 8));
                        rSumSq += (double)s * s;
                    }
                    float rxRms = (float)Math.Sqrt(rSumSq / (data.Length / 2)) / 32768f;
                    _rxLevel = rxRms;
                    if (rxRms > _rxPeak) _rxPeak = rxRms;
                }
                catch (SocketException) { break; }   // socket closed by StopRx
                catch { /* ignore transient errors */ }
            }
        }

        private void StopRx()
        {
            try { _rxUdp?.Close(); } catch { }
            _rxUdp = null;

            _rxThread?.Join(500);
            _rxThread = null;

            try { _rxOut?.Stop(); }       catch { }
            _rxOut?.Dispose();
            _rxOut    = null;
            _rxBuf    = null;

            try { _rxMirrorOut?.Stop(); } catch { }
            _rxMirrorOut?.Dispose();
            _rxMirrorOut  = null;
            _rxMirrorBuf  = null;
        }

        // ── TCP keepalive ─────────────────────────────────────────────────────────
        private void StartTcpKeepalive()
        {
            _tcpThread = new Thread(TcpLoop) { IsBackground = true, Name = "TcpKeepalive" };
            _tcpThread.Start();
        }

        private void TcpLoop()
        {
            int     backoff    = 1_000;
            const int maxBackoff = 30_000;
            byte[]  ping       = System.Text.Encoding.ASCII.GetBytes("PING\n");

            while (_running)
            {
                try
                {
                    using var tcp = new TcpClient();
                    tcp.Connect(_cfg.PiHost, _cfg.TcpPort);
                    Log("TCP keepalive connected");
                    ConnectionChanged?.Invoke(true);
                    backoff = 1_000;   // reset on successful connect

                    using var stream = tcp.GetStream();
                    while (_running)
                    {
                        stream.Write(ping, 0, ping.Length);
                        Thread.Sleep(5_000);
                    }
                }
                catch (Exception ex)
                {
                    ConnectionChanged?.Invoke(false);
                    if (_running)
                    {
                        Log($"TCP disconnected ({ex.Message}). Retry in {backoff / 1000}s…");
                        Thread.Sleep(backoff);
                        backoff = Math.Min(backoff * 2, maxBackoff);
                    }
                }
            }
        }

        private void StopTcp()
        {
            _tcpThread?.Join(500);
            _tcpThread = null;
        }

        // ── helpers ───────────────────────────────────────────────────────────────
        private void Log(string msg) => LogMessage?.Invoke(msg);
    }
}
