using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using POps.Shared;

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
    // Cihaza özel anahtarla (0.1.12+) her kod günde BİR KEZ kabul edilir: kodu gören biri aynı gün yeniden
    // karantinaya alınan cihazı o kodla açamaz. Panel her istekte günün bir sonraki kodunu verir (en çok
    // DeviceBypassSecret.MaxDailyCodes). Kullanılan kodlar da sayaç dosyasında tutulur. Eski ortak anahtarın günde
    // tek kodu olduğu için onda bu sınır yoktur.
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
        private string _usedDate;
        private readonly HashSet<int> _usedCodes = new HashSet<int>();

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
            => Attempt(token, hwId, secret, null, false, localDate);

        public Result Attempt(string token, string hwId, string legacySecret, string deviceSecret,
            bool deviceSecretPresent, DateTime localDate)
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

            bool accepted;
            if (deviceSecretPresent)
            {
                string day = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (_usedDate != day)
                {
                    _usedDate = day;
                    _usedCodes.Clear();
                }
                int n = DeviceBypassSecret.MatchIndex(token, hwId, localDate, deviceSecret, i => _usedCodes.Contains(i));
                accepted = n >= 0;
                if (accepted) _usedCodes.Add(n);
            }
            else
            {
                accepted = Matches(token, hwId, legacySecret, null, false, localDate);
            }

            if (accepted)
            {
                _failures = 0;
                _lockouts = 0;
                Save();
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
            [JsonPropertyName("used_date")] public string UsedDate { get; set; }
            [JsonPropertyName("used_codes")] public int[] UsedCodes { get; set; }
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
                _usedDate = state.UsedDate;
                foreach (int n in state.UsedCodes ?? Array.Empty<int>())
                    if (n >= 0 && n < DeviceBypassSecret.MaxDailyCodes) _usedCodes.Add(n);
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Bypass sayaçları okunamadı: {ex.Message}", true); }
        }

        private void Save()
        {
            if (_statePath == null) return;
            try
            {
                SecureStore.WriteProtected(_statePath, JsonSerializer.Serialize(new State
                {
                    Failures = _failures, Lockouts = _lockouts, LockedUntilUtc = LockedUntilUtc, LastFailureUtc = LastFailureUtc,
                    UsedDate = _usedDate, UsedCodes = _usedCodes.OrderBy(n => n).ToArray(),
                }));
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Bypass sayaçları yazılamadı: {ex.Message}", true); }
        }

        public static bool Matches(string token, string hwId, string secret, DateTime localDate)
            => DeviceBypassSecret.Matches(token, hwId, localDate, null, false, secret);

        public static bool Matches(string token, string hwId, string legacySecret, string deviceSecret,
            bool deviceSecretPresent, DateTime localDate) =>
            DeviceBypassSecret.Matches(token, hwId, localDate, deviceSecret, deviceSecretPresent, legacySecret);
    }
}
