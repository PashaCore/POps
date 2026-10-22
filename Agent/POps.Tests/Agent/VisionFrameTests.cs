using System;
using System.Collections.Generic;
using System.Linq;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // Vision v2: ikili kare başlığı, değişen bölgelerin birleştirilmesi, uyarlanır kalite
    public class VisionFrameTests : TestBase
    {
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

        [Fact]
        public void Header_IsBigEndian_AndRoundTrips()
        {
            byte[] frame = VisionFrame.Build(VisionFrame.Region, 1, 0x01020304, 100, 200, 300, 40, 1920, 1080, Jpeg);
            Assert.Equal(VisionFrame.HeaderSize + Jpeg.Length, frame.Length);
            Assert.Equal(new byte[] { 0x02, 0x01, 0x01, 0x02, 0x03, 0x04, 0x00, 0x64, 0x00, 0xC8, 0x01, 0x2C, 0x00, 0x28, 0x07, 0x80, 0x04, 0x38 }, frame.Take(18));
            Assert.True(VisionFrame.TryParse(frame, out var h));
            Assert.Equal((VisionFrame.Region, (byte)1, 0x01020304u, (ushort)100, (ushort)200, (ushort)300, (ushort)40, (ushort)1920, (ushort)1080, Jpeg.Length),
                (h.Kind, h.Monitor, h.Sequence, h.X, h.Y, h.Width, h.Height, h.FullWidth, h.FullHeight, h.PayloadLength));
        }

        [Fact]
        public void FullFrame_AndCursor_AreValid()
        {
            Assert.True(VisionFrame.TryParse(VisionFrame.Build(VisionFrame.Full, VisionFrame.AllMonitors, 1, 0, 0, 3840, 1080, 3840, 1080, Jpeg), out _));
            Assert.True(VisionFrame.TryParse(VisionFrame.Build(VisionFrame.Cursor, 0, 2, 10, 20, 0, 0, 1920, 1080, ReadOnlySpan<byte>.Empty), out var cursor));
            Assert.Equal(0, cursor.PayloadLength);
        }

        public static IEnumerable<object[]> Invalid() => new[]
        {
            new object[] { VisionFrame.Build(0x04, 0, 1, 0, 0, 10, 10, 10, 10, Jpeg) },                       // tür
            new object[] { VisionFrame.Build(VisionFrame.Full, 0, 1, 1, 0, 10, 10, 10, 10, Jpeg) },          // tam kare 0,0'da değil
            new object[] { VisionFrame.Build(VisionFrame.Full, 0, 1, 0, 0, 9, 10, 10, 10, Jpeg) },           // tam kare çıktının boyutunda değil
            new object[] { VisionFrame.Build(VisionFrame.Region, 0, 1, 5, 5, 6, 5, 10, 10, Jpeg) },          // çıktının dışına taşıyor
            new object[] { VisionFrame.Build(VisionFrame.Region, 0, 1, 0, 0, 0, 5, 10, 10, Jpeg) },          // boş bölge
            new object[] { VisionFrame.Build(VisionFrame.Region, 0, 1, 0, 0, 5, 5, 10, 10, new byte[] { 1, 2, 3, 4 }) },   // JPEG değil
            new object[] { VisionFrame.Build(VisionFrame.Cursor, 0, 1, 1, 1, 0, 0, 10, 10, Jpeg) },          // imleçte görüntü
            new object[] { VisionFrame.Build(VisionFrame.Region, 16, 1, 0, 0, 5, 5, 10, 10, Jpeg) },         // ekran numarası
            new object[] { VisionFrame.Build(VisionFrame.Full, 0, 1, 0, 0, 0, 0, 0, 0, Jpeg) },              // boyutsuz çıktı
            new object[] { new byte[10] },                                                                   // başlıktan kısa
        };

        [Theory]
        [MemberData(nameof(Invalid))]
        public void InvalidFrames_AreRefused(byte[] frame) => Assert.False(VisionFrame.TryParse(frame, out _));

        [Fact]
        public void FramesOverTwoMegabytes_AreRefused()
        {
            byte[] big = new byte[VisionFrame.MaxFrameBytes];
            big[0] = 0xFF; big[1] = 0xD8;
            Assert.False(VisionFrame.TryParse(VisionFrame.Build(VisionFrame.Full, 0, 1, 0, 0, 10, 10, 10, 10, big), out _));
        }

        [Fact]
        public void PipeMagic_SeparatesV2FramesFromTextAndLegacyJpeg()
        {
            byte[] frame = VisionFrame.Build(VisionFrame.Full, 0, 1, 0, 0, 10, 10, 10, 10, Jpeg);
            byte[] onPipe = VisionFrame.WithPipeMagic(frame);
            Assert.True(VisionFrame.HasPipeMagic(onPipe));
            Assert.False(VisionFrame.HasPipeMagic(Jpeg));
            Assert.False(VisionFrame.HasPipeMagic(System.Text.Encoding.UTF8.GetBytes("UNLOCK_BYPASS:1234")));
            Assert.Equal(frame, onPipe.Skip(2));
        }

        [Theory]
        [InlineData("all", VisionFrame.AllMonitors, true)]
        [InlineData("0", 0, true)]
        [InlineData("15", 15, true)]
        [InlineData("16", 0, false)]
        [InlineData("-1", 0, false)]
        [InlineData("ALL", 0, false)]
        [InlineData("1e1", 0, false)]
        public void MonitorSelection(string value, byte expected, bool ok)
        {
            Assert.Equal(ok, VisionFrame.TryParseMonitor(value, out byte monitor));
            if (ok) Assert.Equal(expected, monitor);
        }

        private static VisionRegions.Rect R(int x, int y, int w, int h) => new VisionRegions.Rect(x, y, w, h);

        [Fact]
        public void Regions_TouchingTilesMerge_SeparateOnesStay()
        {
            var merged = VisionRegions.Merge(new[] { R(0, 0, 64, 64), R(64, 0, 64, 64), R(512, 512, 64, 64) }, 1920, 1080);
            Assert.Equal(new[] { R(0, 0, 128, 64), R(512, 512, 64, 64) }, merged);
            Assert.Empty(VisionRegions.Merge(Array.Empty<VisionRegions.Rect>(), 1920, 1080));
        }

        [Fact]
        public void Regions_TooManyOrTooLarge_MeanAFullFrame()
        {
            var scattered = Enumerable.Range(0, 9).Select(i => R(i * 200, 0, 64, 64));
            Assert.Null(VisionRegions.Merge(scattered, 1920, 1080));
            Assert.Null(VisionRegions.Merge(new[] { R(0, 0, 1920, 600) }, 1920, 1080));
            Assert.NotNull(VisionRegions.Merge(new[] { R(0, 0, 1920, 500) }, 1920, 1080));
        }

        [Fact]
        public void Quality_DropsQualityThenScale_AndRecoversWhenQuiet()
        {
            var q = new VisionQuality();
            q.SetTarget(75, 1.0, 8);
            Assert.Equal((60, 1.0, 8), (q.Quality, q.Scale, q.Fps));   // varsayılandan başlar, hedefi aşmaz
            DateTime t = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            q.OnDropped(t); q.OnDropped(t); q.OnDropped(t);
            Assert.Equal((30, 1.0), (q.Quality, q.Scale));
            q.OnDropped(t); q.OnDropped(t);
            Assert.Equal((30, 0.8), (q.Quality, q.Scale));
            for (int i = 0; i < 6; i++) q.OnDropped(t);
            Assert.Equal(0.5, q.Scale);

            q.OnTick(t.AddSeconds(4));
            Assert.Equal(0.5, q.Scale);
            q.OnTick(t.AddSeconds(5));
            Assert.Equal(0.6, q.Scale);
            for (int i = 2; i < 30; i++) q.OnTick(t.AddSeconds(5 * i));
            Assert.Equal((75, 1.0), (q.Quality, q.Scale));
        }

        [Fact]
        public void Quality_TargetsAreClamped_AndFormatRoundTrips()
        {
            var q = new VisionQuality();
            q.SetTarget(5, 3.0, 50);
            Assert.Equal((30, 1.0, 10), (q.TargetQuality, q.TargetScale, q.Fps));
            Assert.Equal("55,0.75,4", VisionQuality.Format(55, 0.75, 4));
            Assert.True(VisionQuality.TryParse("55,0.75,4", out int quality, out double scale, out int fps));
            Assert.Equal((55, 0.75, 4), (quality, scale, fps));
            Assert.False(VisionQuality.TryParse("55;0.75", out _, out _, out _));
        }
    }
}
