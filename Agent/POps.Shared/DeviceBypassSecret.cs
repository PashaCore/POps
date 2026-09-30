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

        public static string Code(byte[] key, string hwId, DateTime localDate)
        {
            if (key == null || key.Length != 32 || string.IsNullOrEmpty(hwId)) return null;
            string message = hwId + "|" + localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).Substring(0, 6);
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
