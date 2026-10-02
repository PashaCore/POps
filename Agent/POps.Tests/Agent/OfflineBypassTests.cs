using System;
using System.Linq;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class OfflineBypassTests : TestBase
    {
        private const string DeviceSecret = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE";

        [Fact]
        public void PerDeviceHmac_MatchesTheContractVector()
        {
            byte[] key = Enumerable.Repeat((byte)1, 32).ToArray();
            DateTime date = new DateTime(2026, 10, 1);

            Assert.Equal("BA258E", DeviceBypassSecret.Code(key, "HW-TEST", date));
            Assert.True(OfflineBypass.Matches("BA258E", "HW-TEST", "legacy", DeviceSecret, true, date));
            Assert.Equal("72cd6e8422c407fb", DeviceBypassSecret.Fingerprint(key));
        }

        // Günün sonraki kodları: "HW-TEST|2026-10-01|n" (sunucu Backend/tests/test_units.py ile ortak vektör)
        [Fact]
        public void LaterDailyCodes_MatchTheContractVector()
        {
            byte[] key = Enumerable.Repeat((byte)1, 32).ToArray();
            DateTime date = new DateTime(2026, 10, 1);
            Assert.Equal("D31B3C", DeviceBypassSecret.Code(key, "HW-TEST", date, 1));
            Assert.Equal("ACA17F", DeviceBypassSecret.Code(key, "HW-TEST", date, 9));
            Assert.Equal(1, DeviceBypassSecret.MatchIndex("D31B3C", "HW-TEST", date, DeviceSecret, _ => false));
            Assert.Equal(-1, DeviceBypassSecret.MatchIndex("BA258E", "HW-TEST", date, DeviceSecret, n => n == 0));
        }

        // Cihaz anahtarlı kod günde bir kez: kodu gören biri aynı gün yeniden kilitlenen cihazı onunla açamaz
        [Fact]
        public void DeviceCode_IsAcceptedOncePerDay_AndTheNextCodeStillWorks()
        {
            DateTime date = new DateTime(2026, 10, 1);
            var guard = new OfflineBypass(() => new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
            Assert.Equal(OfflineBypass.Result.Accepted, guard.Attempt("BA258E", "HW-TEST", null, DeviceSecret, true, date));
            Assert.Equal(OfflineBypass.Result.Rejected, guard.Attempt("BA258E", "HW-TEST", null, DeviceSecret, true, date));
            Assert.Equal(OfflineBypass.Result.Accepted, guard.Attempt("D31B3C", "HW-TEST", null, DeviceSecret, true, date));
            // Ertesi gün kullanılanlar sıfırlanır
            byte[] key = Enumerable.Repeat((byte)1, 32).ToArray();
            string tomorrow = DeviceBypassSecret.Code(key, "HW-TEST", date.AddDays(1));
            Assert.Equal(OfflineBypass.Result.Accepted, guard.Attempt(tomorrow, "HW-TEST", null, DeviceSecret, true, date.AddDays(1)));
        }

        [Fact]
        public void UsedDeviceCodes_SurviveARestart()
        {
            DateTime date = new DateTime(2026, 10, 1);
            string state = System.IO.Path.Combine(TestEnvironment.NewDir("bypass-used"), OfflineBypass.StateFileName);
            var first = new OfflineBypass(() => new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), state);
            Assert.Equal(OfflineBypass.Result.Accepted, first.Attempt("BA258E", "HW-TEST", null, DeviceSecret, true, date));

            var afterRestart = new OfflineBypass(() => new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), state);
            Assert.Equal(OfflineBypass.Result.Rejected, afterRestart.Attempt("BA258E", "HW-TEST", null, DeviceSecret, true, date));
        }

        [Fact]
        public void DeviceFilePresence_DisablesLegacyFallback()
        {
            DateTime date = new DateTime(2026, 9, 26);
            Assert.True(OfflineBypass.Matches("372CC1", "HW-678CC8C5265E", "sekret-Ç-1", null, false, date));
            Assert.False(OfflineBypass.Matches("372CC1", "HW-678CC8C5265E", "sekret-Ç-1", "broken", true, date));
            Assert.False(OfflineBypass.Matches("372CC1", "HW-678CC8C5265E", "sekret-Ç-1", DeviceSecret, true, date));
        }

        // Backend offline_bypass_code() ile üretilmiş vektörler (sunucu ve ajan aynı kodu kabul etmeli)
        [Theory]
        [InlineData("HW-678CC8C5265E", "sekret-Ç-1", "2026-09-26", "372CC1")]
        [InlineData("HW-ABCDEF123456", "x", "2026-01-05", "CC70F1")]
        [InlineData("HW-0", "uzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzunuzun", "2027-12-31", "9AEFB6")]
        public void ServerCode_IsAccepted(string hwId, string secret, string day, string code)
        {
            DateTime date = DateTime.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(OfflineBypass.Matches(code, hwId, secret, date));
            Assert.True(OfflineBypass.Matches(" " + code.ToLowerInvariant() + " ", hwId, secret, date));
            Assert.False(OfflineBypass.Matches(code, hwId, secret, date.AddDays(1)));
            Assert.False(OfflineBypass.Matches(code, hwId + "X", secret, date));
        }

        [Fact]
        public void LongerPrefixIsAccepted_ShorterThanSixIsNot()
        {
            DateTime date = new DateTime(2026, 9, 26);
            string full = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("HW-A" + "s" + "2026-09-26")));
            Assert.True(OfflineBypass.Matches(full.Substring(0, 16), "HW-A", "s", date));
            Assert.False(OfflineBypass.Matches(full.Substring(0, 5), "HW-A", "s", date));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("ZZZZZZ")]
        public void MalformedCode_IsRejected(string code) => Assert.False(OfflineBypass.Matches(code, "HW-A", "s", DateTime.Today));

        [Fact]
        public void EmptySecret_NeverMatches() => Assert.False(OfflineBypass.Matches("372CC1", "HW-678CC8C5265E", "", new DateTime(2026, 9, 26)));

        [Fact]
        public void FiveWrongCodes_LockWithDoublingBackoff()
        {
            DateTime now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
            var guard = new OfflineBypass(() => now);
            DateTime day = new DateTime(2026, 9, 26);
            const string hw = "HW-678CC8C5265E", secret = "sekret-Ç-1", code = "372CC1";

            for (int i = 0; i < 4; i++) Assert.Equal(OfflineBypass.Result.Rejected, guard.Attempt("000000", hw, secret, day));
            Assert.Equal(OfflineBypass.Result.LockedOut, guard.Attempt("000000", hw, secret, day));
            Assert.Equal(now.AddMinutes(15), guard.LockedUntilUtc);
            Assert.Equal(OfflineBypass.Result.Locked, guard.Attempt(code, hw, secret, day)); // kilitliyken doğru kod da değerlendirilmez

            now = now.AddMinutes(16);
            for (int i = 0; i < 5; i++) guard.Attempt("000000", hw, secret, day);
            Assert.Equal(now.AddMinutes(30), guard.LockedUntilUtc);

            now = now.AddMinutes(31);
            Assert.Equal(OfflineBypass.Result.Accepted, guard.Attempt(code, hw, secret, day));
            Assert.Equal(0, guard.Failures);
            for (int i = 0; i < 5; i++) guard.Attempt("000000", hw, secret, day);
            Assert.Equal(now.AddMinutes(15), guard.LockedUntilUtc); // başarıdan sonra merdiven baştan
        }

        [Fact]
        public void Lockout_IsCappedAtOneDay()
        {
            DateTime now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
            var guard = new OfflineBypass(() => now);
            for (int round = 0; round < 12; round++)
            {
                for (int i = 0; i < 5; i++) guard.Attempt("000000", "a", "b", DateTime.Today);
                now = guard.LockedUntilUtc.AddSeconds(1);
            }
            for (int i = 0; i < 5; i++) guard.Attempt("000000", "a", "b", DateTime.Today);
            Assert.Equal(TimeSpan.FromHours(24), guard.LockedUntilUtc - now);
        }
    }
}
