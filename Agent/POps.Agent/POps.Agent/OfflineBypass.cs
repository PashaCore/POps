using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable disable

namespace POpsAgent
{
    // Çevrimdışı (karantinadaki) cihazın ağ izolasyonunu kaldıran bypass kodu.
    // Kod: SHA-256(hw_id + BypassSecret + yyyy-MM-dd)'nin ilk hex karakterleri, büyük harf. Formül Backend
    // offline_bypass_code ile aynıdır; panel 6 karakter üretir, daha uzun kod da aynı özetin öneki olarak kabul
    // edilir. Tepsi boru hattına oturum açan her kullanıcı yazabildiği için deneme sınırlıdır: 5 hatalı
    // denemede kilit (15 dk, her yeni kilitte iki katı, en çok 24 saat). 6 karakterlik alan böylece denenerek
    // bulunamaz. Sayaçlar C:\POpsData\secure\bypass-state.json'da tutulur: servisin ya da makinenin yeniden
    // başlaması kilidi sıfırlamaz. Son kilit bittikten (ya da son hatalı denemeden) sonra 24 saat hatalı deneme olmazsa
    // eski hatalar ve kilitlenmeler unutulur (kilit yeniden 15 dakikadan başlar); yoksa aylar önceki denemeler her yeni
    // kilidi uzatırdı. Kilit biter bitmez denemeye devam eden ise kilidi 24 saate kadar büyütmeye devam eder.
    public sealed class OfflineBypass
    {
        public enum Result { Accepted, Rejected, LockedOut, Locked }

        public const int MaxFailures = 5;
        public const string StateFileName = "bypass-state.json";
        public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(24);
        private readonly Func<DateTime> _utcNow;
        private readonly string _statePath;
        private int _failures;
        private int _lockouts;

        // statePath null ise durum yalnızca bellektedir
        public OfflineBypass(Func<DateTime> utcNow = null, string statePath = null)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _statePath = statePath;
            Load();
        }

        public int Failures => _failures;
        public int Lockouts => _lockouts;
        public DateTime LockedUntilUtc { get; private set; } = DateTime.MinValue;
        public DateTime LastFailureUtc { get; private set; } = DateTime.MinValue;

        public Result Attempt(string token, string hwId, string secret, DateTime localDate)
        {
            DateTime now = _utcNow();
            DateTime quietSince = LastFailureUtc > LockedUntilUtc ? LastFailureUtc : LockedUntilUtc;
            if ((_failures > 0 || _lockouts > 0) && now - quietSince >= ForgetAfter)
            {
                _failures = 0;
                _lockouts = 0;
                Save();
            }
            if (now < LockedUntilUtc) return Result.Locked;

            if (Matches(token, hwId, secret, localDate))
            {
                bool changed = _failures != 0 || _lockouts != 0;
                _failures = 0;
                _lockouts = 0;
                if (changed) Save();
                return Result.Accepted;
            }

            LastFailureUtc = now;
            if (++_failures < MaxFailures)
            {
                Save();
                return Result.Rejected;
            }

            LockedUntilUtc = now + TimeSpan.FromMinutes(Math.Min(15 * Math.Pow(2, _lockouts), 24 * 60));
            _lockouts++;
            _failures = 0;
            Save();
            return Result.LockedOut;
        }

        private sealed class State
        {
            [JsonPropertyName("failures")] public int Failures { get; set; }
            [JsonPropertyName("lockouts")] public int Lockouts { get; set; }
            [JsonPropertyName("locked_until_utc")] public DateTime LockedUntilUtc { get; set; }
            [JsonPropertyName("last_failure_utc")] public DateTime LastFailureUtc { get; set; }
        }

        private void Load()
        {
            if (_statePath == null) return;
            try
            {
                string json = SecureStore.Read(_statePath, requireTrustedOwner: true);
                if (json == null) return;
                State state = JsonSerializer.Deserialize<State>(json);
                _failures = Math.Clamp(state.Failures, 0, MaxFailures - 1);
                _lockouts = Math.Clamp(state.Lockouts, 0, 32);
                LockedUntilUtc = DateTime.SpecifyKind(state.LockedUntilUtc, DateTimeKind.Utc);
                LastFailureUtc = DateTime.SpecifyKind(state.LastFailureUtc, DateTimeKind.Utc);
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Bypass sayaçları okunamadı: {ex.Message}", true); }
        }

        private void Save()
        {
            if (_statePath == null) return;
            try
            {
                SecureStore.WriteProtected(_statePath, JsonSerializer.Serialize(new State { Failures = _failures, Lockouts = _lockouts, LockedUntilUtc = LockedUntilUtc, LastFailureUtc = LastFailureUtc }));
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Bypass sayaçları yazılamadı: {ex.Message}", true); }
        }

        public static bool Matches(string token, string hwId, string secret, DateTime localDate)
        {
            // Biçim kuralı tepsiyle ortak (POps.Shared.BypassCode)
            token = BypassCode.Normalize(token);
            if (string.IsNullOrEmpty(secret) || token == null) return false;
            string raw = $"{hwId}{secret}{localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
            string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(expected.Substring(0, token.Length)));
        }
    }
}
