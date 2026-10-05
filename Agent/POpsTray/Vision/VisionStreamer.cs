using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using POps.Shared;

namespace POpsTray.Vision
{
    // Vision v2 yakalama döngüsü (tepsi, kullanıcı oturumu). Ekran DXGI Masaüstü Çoğaltma ile, olmazsa GDI ile alınır.
    // İlk kare (ve ekran/ölçek değişiminden, düşürülen kareden sonra) tam gider; sonra yalnızca değişen bölgeler
    // (64x64 ızgara karşılaştırması). Ekran seçilebilir ya da "hepsi" yan yana birleştirilir. Kalite ve ölçek
    // görüntüleyicinin istediğine (set_quality) ve servisin bildirdiği düşmelere göre ayarlanır. Kareler borudan
    // PipeMagic ile servise gider. Güvenli masaüstü (UAC onayı, kilit, oturum açma ekranı) etkinken o masaüstü
    // yakalanmaz; görüntüleyiciye bildirim resmi gider (bkz. SecureDesktopNotice, POps.Shared.VisionDesktopGate).
    [SupportedOSPlatform("windows")]
    internal sealed class VisionStreamer : IDisposable
    {
        private const int Tile = 64;

        private readonly Action<byte[]> _send;
        private readonly Action<string> _sendText;
        private readonly object _gate = new object();
        private readonly VisionQuality _quality = new VisionQuality();
        private Thread? _thread;
        private volatile bool _running;
        private volatile bool _forceFull = true;
        private volatile bool _resetSources = true;
        private byte _selected = 0xFE;   // henüz seçilmedi: birincil ekran
        private uint _sequence;
        // Fiziksel ekran düzeni ve birleşik görüntüdeki x konumları (uzaktan imleç için)
        private List<MonitorInfo> _monitors = new List<MonitorInfo>();
        private List<int> _offsets = new List<int>();
        private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");

        public VisionStreamer(Action<byte[]> send, Action<string> sendText)
        {
            _send = send;
            _sendText = sendText;
        }

        public bool Running => _running;

        public void Start(int fps)
        {
            lock (_gate)
            {
                _quality.SetTarget(_quality.TargetQuality, _quality.TargetScale, fps);
                if (_running) return;
                _running = true;
                _forceFull = true;
                _resetSources = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "POps Vision v2" };
                _thread.Start();
            }
        }

        public void Stop() => _running = false;

        public void Select(byte monitor)
        {
            lock (_gate) _selected = monitor;
            _resetSources = true;
            _forceFull = true;
        }

        public void SetQuality(int quality, double scale, int fps)
        {
            lock (_gate)
            {
                bool scaleChanged = Math.Abs(scale - _quality.TargetScale) > 0.001;
                _quality.SetTarget(quality, scale, fps);
                if (scaleChanged) _forceFull = true;
            }
        }

        // Servis kare düşürdü: kalite düşer, sonraki kare tam gider
        public void OnDropped()
        {
            lock (_gate) _quality.OnDropped(DateTime.UtcNow);
            _forceFull = true;
        }

        public void OnDisplaySettingsChanged()
        {
            _resetSources = true;
            _forceFull = true;
        }

        // Görüntüleyicinin (çıktı pikseli) koordinatı -> ekran (fiziksel) koordinatı; çağıran per-monitor DPI bağlamında
        public bool TryMapToScreen(int x, int y, out Point screen)
        {
            screen = Point.Empty;
            lock (_gate)
            {
                if (_monitors.Count == 0) return false;
                if (_selected == VisionFrame.AllMonitors)
                {
                    for (int i = 0; i < _monitors.Count; i++)
                    {
                        Rectangle b = _monitors[i].Bounds;
                        if (x >= _offsets[i] && x < _offsets[i] + b.Width && y < b.Height)
                        {
                            screen = new Point(b.X + x - _offsets[i], b.Y + y);
                            return true;
                        }
                    }
                    return false;
                }
                MonitorInfo m = _monitors[Math.Min(SelectedIndex(), _monitors.Count - 1)];
                screen = new Point(m.Bounds.X + Math.Clamp(x, 0, m.Bounds.Width - 1), m.Bounds.Y + Math.Clamp(y, 0, m.Bounds.Height - 1));
                return true;
            }
        }

        // Görüntüleyiciye ekran listesi: [{"index","width","height","primary"}]
        public static string MonitorsJson(IEnumerable<MonitorInfo> monitors) =>
            JsonSerializer.Serialize(monitors.Select(m => new { index = m.Index, width = m.Bounds.Width, height = m.Bounds.Height, primary = m.Primary }));

        private int SelectedIndex()
        {
            if (_selected < _monitors.Count) return _selected;
            int primary = _monitors.FindIndex(m => m.Primary);
            return primary >= 0 ? primary : 0;
        }

        // ------------------------------------------------------------------ döngü
        private sealed class Source : IDisposable
        {
            public MonitorInfo Monitor = null!;
            public DxgiDuplicator? Dxgi;
            public ScreenImage Image = new ScreenImage();
            public void Dispose() => Dxgi?.Dispose();
        }

        private void Loop()
        {
            IntPtr dpi = Dpi.EnterPerMonitor();
            var sources = new List<Source>();
            var current = new ScreenImage();
            var shown = new ScreenImage();
            var notice = new ScreenImage();
            var desktop = new VisionDesktopGate();
            byte monitor = 0;
            bool gdiFailureLogged = false;
            Point lastCursor = new Point(-1, -1);
            try
            {
                while (_running)
                {
                    var watch = Stopwatch.StartNew();
                    // Güvenli masaüstü (UAC, kilit, oturum açma ekranı) etkinken yakalanacak bir şey yok: donmuş son kare
                    // yerine bildirim resmi gider, masaüstü geri gelince kaynaklar yeniden açılır ve tam kare gider
                    VisionDesktopStep step = desktop.Next(InputDesktop.IsOwn(), _forceFull, DateTime.UtcNow);
                    if (step is VisionDesktopStep.Enter or VisionDesktopStep.Notice or VisionDesktopStep.Wait)
                    {
                        if (step == VisionDesktopStep.Enter) TrayLog.Write("Vision v2: kullanıcının masaüstü görünmüyor (güvenli masaüstü); görüntü yerine bildirim gönderiliyor.");
                        if (step != VisionDesktopStep.Wait && SendNotice(current, monitor, notice)) _forceFull = false;
                        Pace(watch);
                        continue;
                    }
                    if (step == VisionDesktopStep.Resume)
                    {
                        TrayLog.Write("Vision v2: kullanıcının masaüstü geri geldi; yakalama sürüyor.");
                        _resetSources = true;
                        _forceFull = true;
                    }
                    if (_resetSources)
                    {
                        _resetSources = false;
                        sources.ForEach(s => s.Dispose());
                        sources = OpenSources();
                        _forceFull = true;
                    }
                    if (sources.Count == 0) { Thread.Sleep(1000); _resetSources = true; continue; }

                    bool changed = CaptureAll(sources, current, out bool lost, out bool gdiFailed);
                    if (lost) _resetSources = true;
                    if (gdiFailed && !gdiFailureLogged)
                    {
                        TrayLog.Write("Vision v2: GDI yakalaması başarısız; sonraki karede yeniden denenecek.");
                        gdiFailureLogged = true;
                    }
                    monitor = sources.Count == 1 ? (byte)sources[0].Monitor.Index : VisionFrame.AllMonitors;
                    if (current.Width > 0 && (_forceFull || shown.Width != current.Width || shown.Height != current.Height))
                    {
                        if (SendRegion(VisionFrame.Full, monitor, current, new VisionRegions.Rect(0, 0, current.Width, current.Height)))
                        {
                            _forceFull = false;
                            shown.CopyFrom(current);
                        }
                    }
                    else if (changed)
                    {
                        List<VisionRegions.Rect>? regions = VisionRegions.Merge(DirtyTiles(shown, current), current.Width, current.Height);
                        if (regions == null) _forceFull = true;
                        else if (regions.Count > 0)
                        {
                            foreach (var r in regions) SendRegion(VisionFrame.Region, monitor, current, r);
                            shown.CopyFrom(current);
                        }
                    }
                    Point cursor = CursorIn(monitor);
                    if (cursor.X >= 0 && cursor != lastCursor && current.Width > 0 && cursor.X < current.Width && cursor.Y < current.Height)
                    {
                        lastCursor = cursor;
                        _send(VisionFrame.WithPipeMagic(VisionFrame.Build(VisionFrame.Cursor, monitor, NextSequence(), cursor.X, cursor.Y, 0, 0, current.Width, current.Height, ReadOnlySpan<byte>.Empty)));
                    }
                    Pace(watch);
                }
            }
            catch (Exception ex) { TrayLog.Write($"Vision v2 yakalama durdu: {ex.GetType().Name}"); }
            finally
            {
                sources.ForEach(s => s.Dispose());
                Dpi.Restore(dpi);
                _running = false;
            }
        }

        // Kare hızına göre bekler (kalite adımı da burada)
        private void Pace(Stopwatch watch)
        {
            int fps;
            lock (_gate)
            {
                _quality.OnTick(DateTime.UtcNow);
                fps = _quality.Fps;
            }
            int wait = 1000 / fps - (int)watch.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }

        // Kullanıcının masaüstü görünmüyor: son görüntünün boyutunda (yoksa birincil ekranın) bildirim resmi tam kare
        // olarak gider. Dönen: kare gönderildi mi.
        private bool SendNotice(ScreenImage current, byte monitor, ScreenImage notice)
        {
            int width = current.Width, height = current.Height;
            if (width == 0 || height == 0)
            {
                MonitorInfo? primary = Monitors.List().Find(m => m.Primary);
                width = primary?.Bounds.Width ?? 1280;
                height = primary?.Bounds.Height ?? 720;
                monitor = (byte)(primary?.Index ?? 0);
            }
            if (notice.Width != width || notice.Height != height) SecureDesktopNotice.RenderInto(notice, width, height);
            return SendRegion(VisionFrame.Full, monitor, notice, new VisionRegions.Rect(0, 0, width, height));
        }

        // Seçili ekran (ya da hepsi) için kaynaklar; DXGI olmazsa GDI. Ekran listesi görüntüleyiciye gider.
        private List<Source> OpenSources()
        {
            List<MonitorInfo> monitors = Monitors.List();
            List<MonitorInfo> chosen;
            lock (_gate)
            {
                _monitors = monitors;
                _offsets = new List<int>();
                int x = 0;
                foreach (MonitorInfo m in monitors) { _offsets.Add(x); x += m.Bounds.Width; }
                chosen = monitors.Count == 0 ? new List<MonitorInfo>()
                    : _selected == VisionFrame.AllMonitors ? monitors
                    : new List<MonitorInfo> { monitors[SelectedIndex()] };
            }
            _sendText("VISION_MONITORS:" + MonitorsJson(monitors));
            var sources = new List<Source>();
            foreach (MonitorInfo m in chosen)
            {
                DxgiDuplicator? dxgi = DxgiDuplicator.TryCreate(m.DeviceName, out string? error);
                if (dxgi == null) TrayLog.Write($"Vision v2: {m.DeviceName} için GDI kullanılıyor ({error})");
                sources.Add(new Source { Monitor = m, Dxgi = dxgi });
            }
            return sources;
        }

        // Dönen: görüntü değişmiş olabilir mi. lost: bir DXGI kaynağı kayboldu (yeniden açılacak). gdiFailed: GDI
        // yakalaması bu turda başarısız oldu (masaüstü denetimiyle yakalama arasında güvenli masaüstüne geçildiyse BitBlt
        // hata döner; eskiden bu hata yakalama iş parçacığını bitiriyordu ve yayın masaüstü geri gelince de donuk kalıyordu)
        private static bool CaptureAll(List<Source> sources, ScreenImage current, out bool lost, out bool gdiFailed)
        {
            lost = false;
            gdiFailed = false;
            bool changed = false;
            foreach (Source s in sources)
            {
                if (s.Dxgi != null)
                {
                    CaptureResult result = s.Dxgi.TryCapture(s.Image, 0);
                    if (result == CaptureResult.Lost) { lost = true; continue; }
                    changed |= result == CaptureResult.NewFrame;
                }
                else
                {
                    try
                    {
                        GdiCapture.Capture(s.Monitor.Bounds, s.Image);
                        changed = true;
                    }
                    catch (ExternalException) { gdiFailed = true; }
                }
            }
            if (sources.Count == 1)
            {
                if (changed || current.Width == 0) current.CopyFrom(sources[0].Image);
                return changed;
            }
            int width = sources.Sum(s => Math.Max(s.Image.Width, s.Monitor.Bounds.Width));
            int height = sources.Max(s => Math.Max(s.Image.Height, s.Monitor.Bounds.Height));
            current.EnsureSize(width, height);
            int x = 0;
            foreach (Source s in sources)
            {
                if (s.Image.Width > 0) current.Blit(s.Image, x);
                x += Math.Max(s.Image.Width, s.Monitor.Bounds.Width);
            }
            return changed;
        }

        // Gösterilen görüntüden farklı 64x64 kareler
        internal static List<VisionRegions.Rect> DirtyTiles(ScreenImage shown, ScreenImage current)
        {
            var dirty = new List<VisionRegions.Rect>();
            for (int ty = 0; ty < current.Height; ty += Tile)
            {
                int h = Math.Min(Tile, current.Height - ty);
                for (int tx = 0; tx < current.Width; tx += Tile)
                {
                    int w = Math.Min(Tile, current.Width - tx);
                    int bytes = w * 4;
                    for (int row = ty; row < ty + h; row++)
                    {
                        int offset = row * current.Stride + tx * 4;
                        if (!current.Pixels.AsSpan(offset, bytes).SequenceEqual(shown.Pixels.AsSpan(offset, bytes)))
                        {
                            dirty.Add(new VisionRegions.Rect(tx, ty, w, h));
                            break;
                        }
                    }
                }
            }
            return dirty;
        }

        private bool SendRegion(byte kind, byte monitor, ScreenImage image, VisionRegions.Rect r)
        {
            int quality;
            double scale;
            lock (_gate)
            {
                quality = _quality.Quality;
                scale = _quality.Scale;
            }
            byte[]? jpeg = Encode(image, r, quality, scale);
            if (jpeg != null && jpeg.Length > VisionFrame.MaxFrameBytes - VisionFrame.HeaderSize)
                jpeg = Encode(image, r, VisionQuality.MinQuality, Math.Min(scale, 0.5));
            if (jpeg == null || jpeg.Length > VisionFrame.MaxFrameBytes - VisionFrame.HeaderSize)
            {
                _forceFull = true;
                return false;
            }
            _send(VisionFrame.WithPipeMagic(VisionFrame.Build(kind, monitor, NextSequence(), r.X, r.Y, r.Width, r.Height, image.Width, image.Height, jpeg)));
            return true;
        }

        private uint NextSequence() => unchecked(++_sequence);

        internal static byte[]? Encode(ScreenImage image, VisionRegions.Rect r, int quality, double scale)
        {
            if (JpegCodec == null) return null;
            GCHandle pin = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
            try
            {
                IntPtr scan0 = pin.AddrOfPinnedObject() + r.Y * image.Stride + r.X * 4;
                using var region = new Bitmap(r.Width, r.Height, image.Stride, PixelFormat.Format32bppRgb, scan0);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                using var output = new MemoryStream();
                if (scale >= 0.999)
                {
                    region.Save(output, JpegCodec, parameters);
                    return output.ToArray();
                }
                int w = Math.Max(1, (int)Math.Round(r.Width * scale)), h = Math.Max(1, (int)Math.Round(r.Height * scale));
                using var scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g.DrawImage(region, 0, 0, w, h);
                }
                scaled.Save(output, JpegCodec, parameters);
                return output.ToArray();
            }
            catch (Exception ex) when (ex is ExternalException || ex is ArgumentException) { return null; }
            finally { pin.Free(); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        // İmlecin seçili görüntüdeki yeri; görüntünün dışındaysa (-1, -1)
        private Point CursorIn(byte monitor)
        {
            if (!GetCursorPos(out POINT p)) return new Point(-1, -1);
            lock (_gate)
            {
                for (int i = 0; i < _monitors.Count; i++)
                {
                    Rectangle b = _monitors[i].Bounds;
                    if (!b.Contains(p.X, p.Y)) continue;
                    if (monitor == VisionFrame.AllMonitors) return new Point(_offsets[i] + p.X - b.X, p.Y - b.Y);
                    return i == monitor ? new Point(p.X - b.X, p.Y - b.Y) : new Point(-1, -1);
                }
            }
            return new Point(-1, -1);
        }

        public void Dispose() => Stop();
    }
}
