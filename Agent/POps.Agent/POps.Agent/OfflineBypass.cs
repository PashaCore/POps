using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

#nullable disable

namespace POpsAgent
{
    // Çevrimdışı (karantinadaki) cihazın ağ izolasyonunu kaldıran bypass kodu.
    // Kod: SHA-256(hw_id + BypassSecret + yyyy-MM-dd)'nin ilk hex karakterleri, büyük harf. Formül Backend
    // offline_bypass_code ile aynıdır; panel 6 karakter üretir, daha uzun kod da aynı özetin öneki olarak kabul
    // edilir. Tepsi boru hattına oturum açan her kullanıcı yazabildiği için deneme sınırlıdır: 5 hatalı
    // denemede kilit (15 dk, her yeni kilitte iki katı, en çok 24 saat). 6 karakterlik alan böylece denenerek
    // bulunamaz. Durum bellektedir; servisi öğrenci yeniden başlatamaz.
    public sealed class OfflineBypass
    {
        public enum Result { Accepted, Rejected, LockedOut, Locked }

        public const int MaxFailures = 5;
        private static readonly Regex TokenRegex = new Regex("^[0-9A-F]{6,64}$", RegexOptions.Compiled);
        private readonly Func<DateTime> _utcNow;
        private int _failures;
        private int _lockouts;

        public OfflineBypass(Func<DateTime> utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

        public int Failures => _failures;
        public DateTime LockedUntilUtc { get; private set; } = DateTime.MinValue;

        public Result Attempt(string token, string hwId, string secret, DateTime localDate)
        {
            if (_utcNow() < LockedUntilUtc) return Result.Locked;

            if (Matches(token, hwId, secret, localDate))
            {
                _failures = 0;
                _lockouts = 0;
                return Result.Accepted;
            }

            if (++_failures < MaxFailures) return Result.Rejected;

            LockedUntilUtc = _utcNow() + TimeSpan.FromMinutes(Math.Min(15 * Math.Pow(2, _lockouts), 24 * 60));
            _lockouts++;
            _failures = 0;
            return Result.LockedOut;
        }

        public static bool Matches(string token, string hwId, string secret, DateTime localDate)
        {
            token = (token ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(secret) || !TokenRegex.IsMatch(token)) return false;
            string raw = $"{hwId}{secret}{localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
            string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(expected.Substring(0, token.Length)));
        }
    }
}
