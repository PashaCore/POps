using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace POps.Shared
{
    // Vision v2 ikili karesi (/ws/vision/{hw_id}, sunucu server_info'da "vision_binary" duyurursa). Başlık big-endian:
    //   1 bayt  tür: 0x01 tam kare, 0x02 bölge, 0x03 imleç konumu (görüntü yok)
    //   1 bayt  ekran: 0..n, 0xFF "hepsi" (yan yana birleşik görüntü)
    //   4 bayt  sıra numarası (u32)
    //   2+2     x, y   bölgenin sol üstü (tam karede 0,0; imleçte imlecin konumu)
    //   2+2     w, h   bölgenin boyutu (tam karede çıktının boyutu; imleçte 0,0)
    //   2+2     çıktının tam genişliği ve yüksekliği
    //   kalan   JPEG (0x03'te yok)
    // Koordinatlar her zaman çıktının gerçek pikselleridir; JPEG küçültülmüş olabilir (ölçek < 1). Görüntüleyici
    // görüntüyü (x, y, w, h) dikdörtgenine çizer. Tepsiden servise giderken başına PipeMagic eklenir.
    public static class VisionFrame
    {
        public const byte Full = 0x01, Region = 0x02, Cursor = 0x03;
        public const byte AllMonitors = 0xFF;
        public const int HeaderSize = 18;
        public const int MaxFrameBytes = 2 * 1024 * 1024;
        public const int MaxMonitors = 16;

        // Tepsi borusunda ikili kare işareti: metin mesajları ASCII ile, eski tam kareler JPEG (FF D8) ile başlar
        public static readonly byte[] PipeMagic = { 0xFE, 0x56 };

        public readonly struct Header
        {
            public Header(byte kind, byte monitor, uint sequence, ushort x, ushort y, ushort width, ushort height, ushort fullWidth, ushort fullHeight, int payloadLength)
            {
                Kind = kind; Monitor = monitor; Sequence = sequence; X = x; Y = y; Width = width; Height = height;
                FullWidth = fullWidth; FullHeight = fullHeight; PayloadLength = payloadLength;
            }

            public byte Kind { get; }
            public byte Monitor { get; }
            public uint Sequence { get; }
            public ushort X { get; }
            public ushort Y { get; }
            public ushort Width { get; }
            public ushort Height { get; }
            public ushort FullWidth { get; }
            public ushort FullHeight { get; }
            public int PayloadLength { get; }
        }

        public static byte[] Build(byte kind, byte monitor, uint sequence, int x, int y, int width, int height, int fullWidth, int fullHeight, ReadOnlySpan<byte> jpeg)
        {
            byte[] frame = new byte[HeaderSize + jpeg.Length];
            Span<byte> s = frame;
            s[0] = kind;
            s[1] = monitor;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(2), sequence);
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(6), checked((ushort)x));
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(8), checked((ushort)y));
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(10), checked((ushort)width));
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(12), checked((ushort)height));
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(14), checked((ushort)fullWidth));
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(16), checked((ushort)fullHeight));
            jpeg.CopyTo(s.Slice(HeaderSize));
            return frame;
        }

        // Geçerli kare mi: tür, boyut sınırı, bölge çıktının içinde, tam kare tüm çıktı, imleçte görüntü yok, JPEG imzası
        public static bool TryParse(ReadOnlySpan<byte> frame, out Header header)
        {
            header = default;
            if (frame.Length < HeaderSize || frame.Length > MaxFrameBytes) return false;
            byte kind = frame[0];
            byte monitor = frame[1];
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(2));
            ushort x = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(6));
            ushort y = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(8));
            ushort w = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(10));
            ushort h = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12));
            ushort fw = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(14));
            ushort fh = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(16));
            ReadOnlySpan<byte> payload = frame.Slice(HeaderSize);
            if (fw == 0 || fh == 0) return false;
            if (monitor != AllMonitors && monitor >= MaxMonitors) return false;
            switch (kind)
            {
                case Full:
                    if (x != 0 || y != 0 || w != fw || h != fh || !IsJpeg(payload)) return false;
                    break;
                case Region:
                    if (w == 0 || h == 0 || x + w > fw || y + h > fh || !IsJpeg(payload)) return false;
                    break;
                case Cursor:
                    if (w != 0 || h != 0 || x >= fw || y >= fh || payload.Length != 0) return false;
                    break;
                default:
                    return false;
            }
            header = new Header(kind, monitor, sequence, x, y, w, h, fw, fh, payload.Length);
            return true;
        }

        private static bool IsJpeg(ReadOnlySpan<byte> payload) => payload.Length > 3 && payload[0] == 0xFF && payload[1] == 0xD8;

        // Boruda işaretli mi (tepsi -> servis)
        public static bool HasPipeMagic(ReadOnlySpan<byte> data) =>
            data.Length >= PipeMagic.Length && data[0] == PipeMagic[0] && data[1] == PipeMagic[1];

        public static byte[] WithPipeMagic(byte[] frame)
        {
            byte[] data = new byte[PipeMagic.Length + frame.Length];
            PipeMagic.CopyTo(data, 0);
            frame.CopyTo(data, PipeMagic.Length);
            return data;
        }

        // select_monitor: 0..15 ya da "all"; tepsiye "VISION_SELECT:<n|all>"
        public static bool TryParseMonitor(string value, out byte monitor)
        {
            monitor = 0;
            if (string.Equals(value, "all", StringComparison.Ordinal)) { monitor = AllMonitors; return true; }
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < MaxMonitors)
            {
                monitor = (byte)index;
                return true;
            }
            return false;
        }

        public static string MonitorText(byte monitor) =>
            monitor == AllMonitors ? "all" : monitor.ToString(CultureInfo.InvariantCulture);
    }

    // Değişen bölgeler: ızgara karelerinden gelen kirli dikdörtgenler birleştirilir. Çok parçalıysa ya da alanın
    // yarısından fazlası değiştiyse tek tam kare göndermek daha ucuzdur.
    public static class VisionRegions
    {
        public const int MaxRegions = 8;
        public const double FullFrameShare = 0.5;

        public readonly struct Rect : IEquatable<Rect>
        {
            public Rect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }
            public int Right => X + Width;
            public int Bottom => Y + Height;
            public long Area => (long)Width * Height;

            public bool Touches(Rect other) =>
                X <= other.Right && other.X <= Right && Y <= other.Bottom && other.Y <= Bottom;

            public Rect Union(Rect other)
            {
                int x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
                return new Rect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
            }

            public bool Equals(Rect other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
            public override bool Equals(object obj) => obj is Rect r && Equals(r);
            public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
            public static bool operator ==(Rect a, Rect b) => a.Equals(b);
            public static bool operator !=(Rect a, Rect b) => !a.Equals(b);
        }

        // Dönen null: tam kare gönderilmeli. Boş liste: değişiklik yok.
        public static List<Rect> Merge(IEnumerable<Rect> dirty, int width, int height)
        {
            var merged = new List<Rect>();
            foreach (Rect rect in dirty)
            {
                Rect current = rect;
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (int i = 0; i < merged.Count; i++)
                    {
                        if (!merged[i].Touches(current)) continue;
                        current = merged[i].Union(current);
                        merged.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
                merged.Add(current);
            }
            long area = merged.Sum(r => r.Area);
            if (merged.Count > MaxRegions || area > (long)width * height * FullFrameShare) return null;
            return merged;
        }
    }

    // Uyarlanır kalite: görüntüleyicinin istediği (set_quality) üst sınırdır. Servis kare düşürünce önce JPEG kalitesi,
    // sonra ölçek düşer; 5 sn düşme olmazsa adım adım hedefe döner.
    public sealed class VisionQuality
    {
        public const int MinQuality = 30, MaxQuality = 75, DefaultQuality = 60;
        public const double MinScale = 0.5, MaxScale = 1.0, DefaultScale = 1.0;
        public const int MinFps = 1, MaxFps = 10, DefaultFps = 5;
        public static readonly TimeSpan QuietBeforeStepUp = TimeSpan.FromSeconds(5);

        public int TargetQuality { get; private set; } = DefaultQuality;
        public double TargetScale { get; private set; } = DefaultScale;
        public int Fps { get; private set; } = DefaultFps;
        public int Quality { get; private set; } = DefaultQuality;
        public double Scale { get; private set; } = DefaultScale;
        private DateTime _lastChangeUtc = DateTime.MinValue;

        public void SetTarget(int quality, double scale, int fps)
        {
            TargetQuality = Math.Clamp(quality, MinQuality, MaxQuality);
            TargetScale = Math.Round(Math.Clamp(scale, MinScale, MaxScale), 2);
            Fps = Math.Clamp(fps, MinFps, MaxFps);
            Quality = Math.Min(Quality, TargetQuality);
            Scale = Math.Min(Scale, TargetScale);
            if (Quality < MinQuality) Quality = MinQuality;
        }

        public void OnDropped(DateTime nowUtc)
        {
            if (Quality > MinQuality) Quality = Math.Max(MinQuality, Quality - 10);
            else Scale = Math.Max(MinScale, Math.Round(Scale - 0.1, 2));
            _lastChangeUtc = nowUtc;
        }

        // Düşme olmadan geçen her QuietBeforeStepUp'ta bir adım: önce ölçek, sonra kalite hedefe doğru
        public void OnTick(DateTime nowUtc)
        {
            if (nowUtc - _lastChangeUtc < QuietBeforeStepUp) return;
            if (Scale < TargetScale) Scale = Math.Min(TargetScale, Math.Round(Scale + 0.1, 2));
            else if (Quality < TargetQuality) Quality = Math.Min(TargetQuality, Quality + 5);
            else return;
            _lastChangeUtc = nowUtc;
        }

        // Servis -> tepsi "VISION_QUALITY:q,s,fps" (değişmez kültür)
        public static string Format(int quality, double scale, int fps) =>
            string.Create(CultureInfo.InvariantCulture, $"{quality},{scale:0.##},{fps}");

        public static bool TryParse(string text, out int quality, out double scale, out int fps)
        {
            quality = 0; scale = 0; fps = 0;
            string[] parts = (text ?? "").Split(',');
            return parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out quality)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out fps);
        }
    }
}
