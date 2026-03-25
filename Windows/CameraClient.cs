using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace HamBridgeWpf
{
    /// <summary>
    /// Connects to the Pi's MJPEG HTTP stream on port 5003, parses multipart
    /// boundaries, and raises FrameReady with a decoded BitmapImage for each
    /// JPEG frame.  Runs entirely on a background thread; events are raised
    /// on that thread — callers must Dispatcher.Invoke to update UI.
    /// </summary>
    public sealed class CameraClient : IDisposable
    {
        private readonly AppConfig          _cfg;
        private CancellationTokenSource?    _cts;
        private Task?                       _task;

        // ── events ────────────────────────────────────────────────────────────
        public event Action<BitmapImage>? FrameReady;
        public event Action<string>?      StatusChanged;
        public event Action<string>?      LogMessage;

        public CameraClient(AppConfig cfg) => _cfg = cfg;

        // ── lifecycle ─────────────────────────────────────────────────────────
        public void Start()
        {
            Stop();
            _cts  = new CancellationTokenSource();
            _task = Task.Run(() => StreamLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _task?.Wait(2000); } catch { }
            _cts  = null;
            _task = null;
        }

        public void Dispose() => Stop();

        // ── stream loop (background thread) ───────────────────────────────────
        private async Task StreamLoop(CancellationToken ct)
        {
            int backoff = 1_000;
            const int maxBackoff = 15_000;

            while (!ct.IsCancellationRequested)
            {
                string url = $"http://{_cfg.PiHost}:{_cfg.CameraPort}/stream";
                Status("Connecting…");
                Log($"Camera connecting to {url}");

                try
                {
                    using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    using var resp    = await http.GetAsync(url,
                                            HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    // Parse the boundary from Content-Type:
                    //   multipart/x-mixed-replace; boundary=frame
                    string boundary = "--frame";
                    string? ct2 = resp.Content.Headers.ContentType?.ToString();
                    if (ct2 != null)
                    {
                        int bi = ct2.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
                        if (bi >= 0)
                            boundary = "--" + ct2[(bi + 9)..].Trim();
                    }

                    Status("Streaming");
                    Log("Camera stream started");
                    backoff = 1_000;

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    await ReadMjpeg(stream, boundary, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Status("Disconnected");
                    Log($"Camera error: {ex.Message}. Retry in {backoff / 1000}s…");
                    try { await Task.Delay(backoff, ct); } catch { break; }
                    backoff = Math.Min(backoff * 2, maxBackoff);
                }
            }

            Status("Stopped");
        }

        /// <summary>
        /// Reads an MJPEG multipart stream, extracting JPEG payloads and
        /// raising FrameReady for each one.
        /// </summary>
        private async Task ReadMjpeg(Stream stream, string boundary,
                                      CancellationToken ct)
        {
            // Buffer approach: read line-by-line for headers,
            // then read Content-Length bytes for the JPEG body.
            using var reader = new BoundaryStreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                // Skip lines until we see the boundary
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(ct);
                    if (line == null) return;   // stream ended
                }
                while (!line.StartsWith(boundary));

                // Read headers until blank line
                int contentLength = 0;
                while (true)
                {
                    line = await reader.ReadLineAsync(ct);
                    if (line == null) return;
                    if (line.Length == 0) break;    // blank line = end of headers
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line[15..].Trim(), out contentLength);
                    }
                }

                if (contentLength <= 0) continue;

                // Read exactly contentLength bytes for the JPEG
                byte[] jpeg = new byte[contentLength];
                int read = 0;
                while (read < contentLength && !ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(jpeg.AsMemory(read, contentLength - read), ct);
                    if (n == 0) return;
                    read += n;
                }

                DecodeAndRaise(jpeg);
            }
        }

        private void DecodeAndRaise(byte[] jpeg)
        {
            try
            {
                // BitmapImage must be created, loaded, and frozen on the same thread
                // before it can be used on the UI thread.
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption   = BitmapCacheOption.OnLoad;
                bmp.StreamSource  = new MemoryStream(jpeg);
                bmp.EndInit();
                bmp.Freeze();   // makes it cross-thread safe
                FrameReady?.Invoke(bmp);
            }
            catch { /* corrupt JPEG — skip */ }
        }

        // ── helpers ───────────────────────────────────────────────────────────
        private void Status(string s) => StatusChanged?.Invoke(s);
        private void Log(string s)    => LogMessage?.Invoke(s);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Minimal line reader that works on a raw network stream.
    // ReadLineAsync reads bytes until \n, strips \r, returns the string.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class BoundaryStreamReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _oneByte = new byte[1];

        public BoundaryStreamReader(Stream stream) => _stream = stream;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var sb = new System.Text.StringBuilder(128);
            while (true)
            {
                int n = await _stream.ReadAsync(_oneByte, 0, 1, ct);
                if (n == 0) return null;    // stream closed
                char c = (char)_oneByte[0];
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append(c);
            }
        }

        public void Dispose() { }
    }
}
