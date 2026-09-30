using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Sunucu modeli: Backend/pops/models.py SoftwareItem / SoftwareInventoryInput
    public sealed class SoftwareItem
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("publisher")] public string Publisher { get; set; }
        [JsonPropertyName("install_date")] public string InstallDate { get; set; }
    }

    public sealed class SoftwareInventoryPayload
    {
        [JsonPropertyName("items")] public List<SoftwareItem> Items { get; set; } = new List<SoftwareItem>();
    }

    // Kurulu yazılımlar: "Programlar ve Özellikler"in kaynağı olan Uninstall anahtarları.
    //  * HKLM (64 bit) ve WOW6432Node (32 bit uygulamalar), ayrıca oturumu açık kullanıcıların hive'ları
    //    (HKU\<SID>: yalnızca o kullanıcıya kurulan uygulamalar, ör. kullanıcı kurulumu VS Code, Zoom).
    //  * Windows bileşenleri ve güncelleme kayıtları atlanır: SystemComponent=1, ParentKeyName (başka bir ürünün
    //    parçası), ReleaseType Update/Hotfix/Security Update, boş DisplayName.
    //  * Liste (ad, sürüm) çiftine göre tekilleştirilip sıralanır; aynı kurulum her zaman aynı özeti verir.
    [SupportedOSPlatform("windows")]
    public static class SoftwareInventory
    {
        public const int MaxItems = 5000;
        // Sunucu sütun sınırları (Backend/pops/routers/inventory.py); ajan da kısaltır, özet sunucunun sakladığıyla aynı olsun
        public const int MaxName = 300, MaxVersion = 100, MaxPublisher = 200, MaxInstallDate = 20;
        public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        public const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        private static readonly string[] SkippedReleaseTypes = { "Update", "Hotfix", "Security Update" };
        private static readonly string[] ValueNames = { "DisplayName", "DisplayVersion", "Publisher", "InstallDate", "SystemComponent", "ParentKeyName", "ReleaseType" };

        // Bir Uninstall alt anahtarının değerleri -> kayıt; atlanacaksa null
        public static SoftwareItem FromEntry(IReadOnlyDictionary<string, object> values)
        {
            string name = Text(values, "DisplayName");
            if (name == null) return null;
            if (Number(values, "SystemComponent") == 1) return null;
            if (Text(values, "ParentKeyName") != null) return null;
            string releaseType = Text(values, "ReleaseType");
            if (releaseType != null && SkippedReleaseTypes.Any(t => string.Equals(t, releaseType, StringComparison.OrdinalIgnoreCase))) return null;

            return new SoftwareItem
            {
                Name = Clip(name, MaxName),
                Version = Clip(Text(values, "DisplayVersion"), MaxVersion) ?? "",
                Publisher = Clip(Text(values, "Publisher"), MaxPublisher),
                // YYYYMMDD olduğu gibi (bazı kurulumlar başka biçim yazar; sunucu metin olarak saklar)
                InstallDate = Clip(Text(values, "InstallDate"), MaxInstallDate),
            };
        }

        public static List<SoftwareItem> Build(IEnumerable<IReadOnlyDictionary<string, object>> entries)
        {
            var byKey = new Dictionary<(string, string), SoftwareItem>();
            foreach (var entry in entries)
            {
                SoftwareItem item = FromEntry(entry);
                if (item == null) continue;
                if (byKey.TryGetValue((item.Name, item.Version), out SoftwareItem existing))
                {
                    existing.Publisher ??= item.Publisher;
                    existing.InstallDate ??= item.InstallDate;
                }
                else byKey[(item.Name, item.Version)] = item;
            }
            return byKey.Values
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.Ordinal)
                .ThenBy(i => i.Version, StringComparer.Ordinal)
                .Take(MaxItems)
                .ToList();
        }

        public static string Hash(List<SoftwareItem> items) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SoftwareInventoryPayload { Items = items }))));

        // Yerel ve etki alanı kullanıcıları (S-1-5-21-…); "_Classes" hive'ları ve hizmet hesapları (S-1-5-18/19/20) atlanır
        public static bool IsUserHive(string name) =>
            name != null && name.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase);

        public static List<SoftwareItem> Collect() => Build(ReadRegistry());

        public static List<IReadOnlyDictionary<string, object>> ReadRegistry()
        {
            var entries = new List<IReadOnlyDictionary<string, object>>();
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                ReadUninstallKey(hklm, UninstallPath, entries);
                ReadUninstallKey(hklm, UninstallPath32, entries);
            }
            using (RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
            {
                foreach (string sid in users.GetSubKeyNames().Where(IsUserHive))
                    ReadUninstallKey(users, sid + "\\" + UninstallPath, entries);
            }
            return entries;
        }

        private static void ReadUninstallKey(RegistryKey root, string path, List<IReadOnlyDictionary<string, object>> entries)
        {
            try
            {
                using RegistryKey key = root.OpenSubKey(path);
                if (key == null) return;
                foreach (string sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey app = key.OpenSubKey(sub);
                        if (app == null) continue;
                        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (string name in ValueNames)
                        {
                            object value = app.GetValue(name);
                            if (value != null) values[name] = value;
                        }
                        entries.Add(values);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string Text(IReadOnlyDictionary<string, object> values, string name)
        {
            if (!values.TryGetValue(name, out object value) || value == null) return null;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        public static string Clip(string value, int maxLength) =>
            value == null || value.Length <= maxLength ? value : value.Substring(0, maxLength).TrimEnd();

        private static long? Number(IReadOnlyDictionary<string, object> values, string name)
        {
            if (!values.TryGetValue(name, out object value) || value == null) return null;
            if (value is int i) return i;
            if (value is long l) return l;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null;
        }
    }

    // Açılıştan kısa süre sonra, sonra 6 saatte bir liste okunur. Değişmediyse gönderilmez; yine de günde en az bir
    // kez gönderilir (sunucu listeyi her seferinde bütünüyle değiştirir). Gönderim başarısızsa 15 dk sonra yeniden.
    [SupportedOSPlatform("windows")]
    public sealed class SoftwareReporter
    {
        public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
        public static readonly TimeSpan MaxSilence = TimeSpan.FromDays(1);
        public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan WaitForSecret = TimeSpan.FromMinutes(1);

        private readonly ReportGate _gate = new ReportGate(MaxSilence);
        private readonly string _serverUrl;
        private readonly Func<string> _hwId;
        private readonly Action _uploaded;
        private readonly Action<string> _error;

        public SoftwareReporter(string serverUrl, Func<string> hwId, Action uploaded = null, Action<string> error = null)
        {
            _serverUrl = serverUrl;
            _hwId = hwId;
            _uploaded = uploaded ?? (() => { });
            _error = error ?? (_ => { });
            Poster = (id, payload) => AgentHttp.PostAsync(_serverUrl, AgentHttp.DevicePath("/api/software/", id), id, payload, "Yazılım envanteri");
        }

        // İşletim sistemi ve ağ sınırları testlerde sahteleriyle değiştirilir.
        internal Func<List<SoftwareItem>> Collector { get; set; } = SoftwareInventory.Collect;
        internal Func<string, SoftwareInventoryPayload, Task<PostResult>> Poster { get; set; }
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public async Task RunAsync(CancellationToken token)
        {
            await Task.Delay(StartupDelay, token);
            while (!token.IsCancellationRequested)
            {
                TimeSpan wait = await ReportOnceAsync() ?? Interval;
                await Task.Delay(wait, token);
            }
        }

        // Başarısız gönderimden sonra: sunucuda uç yoksa (eski sunucu) bir gün, secret yoksa bir dakika, aksi halde 15 dk
        public static TimeSpan DelayAfter(PostResult result) => result switch
        {
            PostResult.EndpointMissing => MaxSilence,
            PostResult.NotSent => WaitForSecret,
            _ => RetryDelay,
        };

        // Bir tur; null: normal aralık, aksi halde bir sonraki denemeye kadar beklenecek süre
        internal async Task<TimeSpan?> ReportOnceAsync()
        {
            if (!AgentHttp.EnsureCanReport()) return WaitForSecret;
            try
            {
                List<SoftwareItem> items = Collector();
                string hash = SoftwareInventory.Hash(items);
                DateTime now = UtcNow();
                if (!_gate.ShouldSend(hash, now)) return null;

                string hwId = _hwId();
                PostResult result = await Poster(hwId, new SoftwareInventoryPayload { Items = items });
                if (result != PostResult.Sent) return DelayAfter(result);
                _gate.MarkSent(hash, now);
                _uploaded();
                POpsHelpers.Log("AGENT", $"Yazılım envanteri gönderildi ({items.Count} kayıt).");
                return null;
            }
            catch (Exception ex)
            {
                _error(ex.Message);
                POpsHelpers.Log("AGENT", $"Yazılım envanteri okunamadı: {ex.Message}", true);
                return RetryDelay;
            }
        }
    }
}
