using System;
using System.Linq;
using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // Komut tüneline yeniden bağlanma: üstel geri çekilme + full jitter (0.1.7'ye kadar sabit 5 sn; 500 ajan sunucu
    // yeniden başlayınca aynı anda geliyordu, bkz. BENCHMARKS.md)
    public class ReconnectBackoffTests : TestBase
    {
        // NextDouble() hep aynı değeri döner
        private sealed class FixedRandom : Random
        {
            private readonly double _value;
            public FixedRandom(double value) { _value = value; }
            public override double NextDouble() => _value;
        }

        private static readonly Random Top = new FixedRandom(0.9999);
        private static readonly Random Bottom = new FixedRandom(0);

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 4)]
        [InlineData(2, 8)]
        [InlineData(3, 16)]
        [InlineData(4, 32)]
        [InlineData(5, 60)]
        [InlineData(6, 60)]
        [InlineData(int.MaxValue, 60)]
        [InlineData(-3, 2)]
        public void Ceiling_DoublesFrom2sUpTo60s(int attempt, int seconds) =>
            Assert.Equal(TimeSpan.FromSeconds(seconds), ReconnectBackoff.Ceiling(attempt));

        [Fact]
        public void Delay_IsFullJitterBelowTheCeiling()
        {
            for (int attempt = 0; attempt <= 8; attempt++)
            {
                TimeSpan ceiling = ReconnectBackoff.Ceiling(attempt);
                Assert.Equal(TimeSpan.Zero, ReconnectBackoff.Delay(attempt, false, Bottom));
                TimeSpan top = ReconnectBackoff.Delay(attempt, false, Top);
                Assert.InRange(top.TotalMilliseconds, ceiling.TotalMilliseconds * 0.999, ceiling.TotalMilliseconds - 0.1);
                Assert.Equal(ceiling / 2, ReconnectBackoff.Delay(attempt, false, new FixedRandom(0.5)));
            }
        }

        [Fact]
        public void Delay_SpreadsAgentsOverTheWholeWindow()
        {
            var random = new Random(4242);
            double[] waits = Enumerable.Range(0, 2000).Select(_ => ReconnectBackoff.Delay(5, false, random).TotalSeconds).ToArray();
            Assert.All(waits, w => Assert.InRange(w, 0, 60));
            // Aynı saniyede toplanmazlar: 60 sn'lik pencerenin her dörtte biri dolu
            for (int quarter = 0; quarter < 4; quarter++)
                Assert.Contains(waits, w => w >= quarter * 15 && w < (quarter + 1) * 15);
        }

        [Fact]
        public void AuthRejected_WaitsAtLeast60sPlusJitter()
        {
            Assert.Equal(TimeSpan.FromSeconds(60), ReconnectBackoff.Delay(0, true, Bottom));
            Assert.Equal(TimeSpan.FromSeconds(61), ReconnectBackoff.Delay(0, true, new FixedRandom(0.5)));
            Assert.Equal(TimeSpan.FromSeconds(90), ReconnectBackoff.Delay(5, true, new FixedRandom(0.5)));
            var random = new Random(7);
            for (int i = 0; i < 500; i++)
                Assert.InRange(ReconnectBackoff.Delay(i % 8, true, random).TotalSeconds, 60, 120);
        }

        [Fact]
        public void NextAttempt_GrowsThenStaysAtTheCap()
        {
            int attempt = 0;
            int[] seen = Enumerable.Range(0, 10).Select(_ => attempt = ReconnectBackoff.NextAttempt(attempt)).ToArray();
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 5, 5, 5, 5, 5 }, seen);
            Assert.Equal(ReconnectBackoff.MaxAttempt, ReconnectBackoff.NextAttempt(int.MaxValue));
            Assert.Equal(1, ReconnectBackoff.NextAttempt(-1));
        }

        // Başarılı bağlantıdan sonra sayaç 0'a döner: ilk kopuşta en çok 2 sn beklenir
        [Fact]
        public void AfterAHealthyConnection_FirstRetryIsQuick() =>
            Assert.True(ReconnectBackoff.Delay(0, false, Top) < TimeSpan.FromSeconds(2));

        [Fact]
        public void CloseCode4409_WaitsTenMinutesPlusJitter()
        {
            Assert.Equal(ReconnectBackoff.Rejection.Clone, ReconnectBackoff.FromCloseStatus(4409));
            Assert.Equal(ReconnectBackoff.Rejection.Auth, ReconnectBackoff.FromCloseStatus(4401));
            Assert.Equal(ReconnectBackoff.Rejection.None, ReconnectBackoff.FromCloseStatus(1000));
            Assert.Equal(ReconnectBackoff.Rejection.None, ReconnectBackoff.FromCloseStatus(null));

            for (int attempt = 0; attempt <= ReconnectBackoff.MaxAttempt; attempt++)
            {
                Assert.Equal(TimeSpan.FromMinutes(10), ReconnectBackoff.Delay(attempt, ReconnectBackoff.Rejection.Clone, new FixedRandom(0)));
                TimeSpan top = ReconnectBackoff.Delay(attempt, ReconnectBackoff.Rejection.Clone, new FixedRandom(0.9999));
                Assert.InRange(top, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11));
            }
            Assert.Equal(TimeSpan.FromMinutes(10) + ReconnectBackoff.Ceiling(5) * 0.5,
                ReconnectBackoff.Delay(5, ReconnectBackoff.Rejection.Clone, new FixedRandom(0.5)));
            // Eski imza değişmedi
            Assert.Equal(ReconnectBackoff.AuthRejectedMinimum, ReconnectBackoff.Delay(0, true, new FixedRandom(0)));
        }
    }
}
