#nullable disable
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace POps.Shared
{
    // Sunucunun her cihaz için ürettiği 32 baytlık çevrimdışı bypass anahtarı ve ortak kod formülü.
    public static class DeviceBypassSecret
    {
        // Bir cihazın bir günde kullanabileceği kod sayısı. Günün ilk kodu (n=0) "hw_id|tarih", sonrakiler
        // "hw_id|tarih|n" üzerinden üretilir; ajan her kodu günde bir kez kabul eder (bkz. OfflineBypass). Sunucu
        // Backend/pops/bypass.py ile aynı.
        public const int MaxDailyCodes = 10;

        private static readonly Regex EncodedPattern = new Regex("^[A-Za-z0-9_-]{43}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool TryDecode(string encoded, out byte[] key)
        {
            key = null;
            string value = encoded?.Trim();
            if (value == null || !EncodedPattern.IsMatch(value)) return false;
            try
            {
                byte[] decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
                if (decoded.Length != 32) return false;
                string canonical = Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (!string.Equals(value, canonical, StringComparison.Ordinal)) return false;
                key = decoded;
                return true;
            }
            catch (FormatException) { return false; }
        }

        public static string Code(byte[] key, string hwId, DateTime localDate) => Code(key, hwId, localDate, 0);

        public static string Code(byte[] key, string hwId, DateTime localDate, int n)
        {
            if (key == null || key.Length != 32 || string.IsNullOrEmpty(hwId)) return null;
            return FullHex(key, hwId, localDate, n).Substring(0, 6);
        }

        private static string FullHex(byte[] key, string hwId, DateTime localDate, int n)
        {
            string message = hwId + "|" + localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (n > 0) message += "|" + n.ToString(CultureInfo.InvariantCulture);
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
        }

        // Cihaz anahtarlı kod günün hangi kodu (n), kullanılmamışsa; eşleşmezse ya da kullanılmışsa -1.
        public static int MatchIndex(string token, string hwId, DateTime localDate, string encodedDeviceSecret,
            Func<int, bool> isUsed)
        {
            token = BypassCode.Normalize(token);
            if (token == null || string.IsNullOrEmpty(hwId) || !TryDecode(encodedDeviceSecret, out byte[] key)) return -1;
            byte[] given = Encoding.ASCII.GetBytes(token);
            int found = -1;
            for (int n = 0; n < MaxDailyCodes; n++)
            {
                // Sabit zamanlı karşılaştırma; hangi n'in eşleştiği döngü süresinden anlaşılmasın diye hepsi denenir
                string expected = FullHex(key, hwId, localDate, n);
                bool same = CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(expected.Substring(0, token.Length)));
                if (same && found < 0 && (isUsed == null || !isUsed(n))) found = n;
            }
            return found;
        }

        public static string Fingerprint(byte[] key)
        {
            if (key == null || key.Length != 32) return null;
            return Convert.ToHexString(SHA256.HashData(key)).Substring(0, 16).ToLowerInvariant();
        }

        public static bool Matches(string token, string hwId, DateTime localDate, string encodedDeviceSecret,
            bool deviceSecretPresent, string legacySecret)
        {
            token = BypassCode.Normalize(token);
            if (token == null) return false;

            string expected;
            if (deviceSecretPresent)
            {
                if (!TryDecode(encodedDeviceSecret, out byte[] key)) return false;
                using var hmac = new HMACSHA256(key);
                string message = hwId + "|" + localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
            }
            else
            {
                if (string.IsNullOrEmpty(legacySecret)) return false;
                string raw = hwId + legacySecret + localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            }

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(token),
                Encoding.ASCII.GetBytes(expected.Substring(0, token.Length)));
        }
    }
}
