using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // DNS tabanlı içerik politikası (eşleştirme: bkz. DnsWatch). 15 sn'de bir Windows DNS istemci önbelleğini okur;
    // okulun politikasında (dns_domains) listelenen alan adlarına ya da alt alanlarına uyan adları /api/policy_alert
    // ile bildirir, auto_quarantine açıksa eşikte cihazı karantinaya alır. Liste yoksa ya da boşsa hiçbir şey
    // işaretlenmez.
    //
    // Sayım (otomatik karantina bir öğrenciyi yanlışlıkla yalıtmasın diye):
    //  * Bir ihlal = listedeki bir GİRİŞ. www.site, cdn.site, static.site aynı girişe (site) düşer ve 1 sayılır;
    //    her giriş oturum başına bir kez bildirilir (ilk görülen alan adıyla).
    //  * Eşik son 1 saatteki ihlallere uygulanır (kayan pencere).
    //  * Konsoldaki kullanıcı değişince sayaç ve bildirilenler sıfırlanır; önbellekte o anda duran adlar önceki
    //    kullanıcıya aittir, önbellekten düşene kadar yeni kullanıcıya yazılmaz. Aynı siteyi ikinci öğrenci
    //    açınca yine bildirilir.
    //
    // Servis komut tüneli ilk kez kurulunca başlatılır (Worker.OnCommandChannelConnected); politika her dakika
    // yenilenir (Configure). Eski AdvancedActivityTracker hiç başlatılmıyordu; başlatılsaydı DNS'in yanında
    // hiçbir yere gönderilmeyen dosya/USB/süreç/ağ olaylarını toplayacak ve makinenin uykuya geçmesini
    // engelleyecekti. Yalnızca DNS izleme buraya taşındı.
    [SupportedOSPlatform("windows")]
    public static class DnsPolicyMonitor
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan Window = TimeSpan.FromHours(1);
        private const int MaxRemembered = 10000;

        private static readonly object Sync = new object();
        private static Timer _timer;
        private static AgentPolicy _policy = new AgentPolicy();
        private static string _hwId = "";
        private static string _serverUrl = "";
        // Bu oturumda bildirilen girişler ("kategori|giriş")
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        // Pencere içindeki ihlaller (zaman, kategori)
        private static readonly List<(DateTime At, string Category)> Recent = new List<(DateTime, string)>();
        // Kullanıcı değiştiğinde önbellekte olan adlar (önceki kullanıcının)
        private static HashSet<string> _baseline = new HashSet<string>(StringComparer.Ordinal);
        private static bool _quarantined;
        private static bool _listMissingLogged;
        private static int _checking;

        // Testlerde değiştirilir: saat, önbellek okuyucu, ihlal bildirimi, karantina (neden metniyle)
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
        internal static Func<IEnumerable<string>> CacheReader { get; set; } = DnsWatch.ReadCacheNames;
        internal static Action<string, string> Reporter { get; set; } = ReportViolation;
        // Worker bunu QuarantineControl.LockdownAsync'e bağlar (kilit ekranı + yalıtım + denetim kaydı)
        public static Action<string> Quarantine { get; set; } = reason => { _ = NetworkIsolation.EnableAsync(ServerUrl); };

        private static string ServerUrl { get { lock (Sync) return _serverUrl; } }

        public static bool IsRunning { get { lock (Sync) return _timer != null; } }

        // Son 1 saatteki ihlal sayısı
        public static int Violations
        {
            get
            {
                lock (Sync)
                {
                    Prune(UtcNow());
                    return Recent.Count;
                }
            }
        }

        public static void Configure(AgentPolicy policy, string hwId, string serverUrl)
        {
            lock (Sync)
            {
                _policy = policy ?? new AgentPolicy();
                _hwId = hwId ?? "";
                _serverUrl = serverUrl ?? "";
            }
        }

        // Birden çok çağrılabilir; yalnızca ilki başlatır
        public static void Start()
        {
            lock (Sync)
            {
                if (_timer != null) return;
                _timer = new Timer(_ => CheckNow(), null, Interval, Interval);
            }
            POpsHelpers.Log("TRACKER", "DNS politika izleme başlatıldı.");
        }

        public static void Stop()
        {
            lock (Sync)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        // Karantina kaldırılınca (unlock ya da çevrimdışı bypass) sayaç sıfırlanır; eşik yeniden aşılırsa yine karantina
        public static void ResetViolations()
        {
            lock (Sync)
            {
                Recent.Clear();
                _quarantined = false;
            }
        }

        // Konsoldaki kullanıcı değişti (SessionReporter.UserChanged)
        public static void OnUserChanged()
        {
            HashSet<string> present;
            try { present = new HashSet<string>(CacheReader().Select(DnsWatch.Normalize).Where(n => n != null), StringComparer.Ordinal); }
            catch { present = new HashSet<string>(StringComparer.Ordinal); }
            lock (Sync)
            {
                Reported.Clear();
                Recent.Clear();
                _baseline = present;
            }
        }

        // Testler için: bütün durumu sıfırlar
        internal static void Reset()
        {
            Stop();
            lock (Sync)
            {
                _policy = new AgentPolicy();
                Reported.Clear();
                Recent.Clear();
                _baseline = new HashSet<string>(StringComparer.Ordinal);
                _quarantined = false;
                _listMissingLogged = false;
            }
        }

        private static void Prune(DateTime now) => Recent.RemoveAll(v => now - v.At >= Window);

        // Bir tarama turu; bu turda bildirilen yeni ihlaller (ilk görülen alan adı, kategori)
        public static List<(string Domain, string Category)> CheckNow()
        {
            var found = new List<(string, string)>();
            if (Interlocked.Exchange(ref _checking, 1) == 1) return found;
            try
            {
                AgentPolicy policy;
                lock (Sync) policy = _policy;
                if (policy?.dns_categories == null || policy.dns_categories.Count == 0) return found;
                if (policy.dns_domains == null || policy.dns_domains.Count == 0)
                {
                    if (!_listMissingLogged) POpsHelpers.Log("TRACKER", "DNS kategorileri açık ama politikada alan adı listesi (dns_domains) yok; DNS tespiti yapılmıyor.");
                    _listMissingLogged = true;
                    return found;
                }
                _listMissingLogged = false;

                List<string> names = CacheReader().Select(DnsWatch.Normalize).Where(n => n != null).Distinct().ToList();
                lock (Sync) _baseline.IntersectWith(names);   // önbellekten düşen ad yeniden görülürse yeni ziyarettir

                foreach (string domain in names)
                {
                    var match = DnsWatch.Match(domain, policy.dns_categories, policy.dns_domains);
                    if (match == null) continue;
                    (string category, string entry) = match.Value;

                    string quarantineReason = null;
                    lock (Sync)
                    {
                        if (_baseline.Contains(domain)) continue;
                        if (Reported.Count >= MaxRemembered) Reported.Clear();
                        if (!Reported.Add(category + "|" + entry)) continue;
                        DateTime now = UtcNow();
                        Prune(now);
                        Recent.Add((now, category));
                        int threshold = Math.Max(1, policy.quarantine_threshold);
                        if (policy.auto_quarantine && !_quarantined && Recent.Count >= threshold)
                        {
                            _quarantined = true;
                            quarantineReason = $"{Recent.Count} ihlal / {Window.TotalHours:0} saat ({string.Join(", ", Recent.Select(v => v.Category).Distinct())})";
                        }
                    }

                    found.Add((domain, category));
                    POpsHelpers.Log("TRACKER", $"DNS kural ihlali: {domain} ({category}).");
                    Reporter(domain, category);
                    if (quarantineReason != null)
                    {
                        // Sunucuya ulaşılamasa da eşik uygulanır
                        POpsHelpers.Log("TRACKER", $"Karantina eşiği aşıldı ({quarantineReason}); cihaz karantinaya alınıyor.");
                        Quarantine(quarantineReason);
                    }
                }
            }
            catch (Exception ex) { POpsHelpers.Log("TRACKER", $"DNS önbelleği okunamadı: {ex.Message}", true); }
            finally { Interlocked.Exchange(ref _checking, 0); }
            return found;
        }

        // Sunucu modeli: Backend/pops/models.py PolicyAlertInput
        public sealed class PolicyAlertPayload
        {
            [JsonPropertyName("hw_id")] public string HwId { get; set; }
            [JsonPropertyName("domain")] public string Domain { get; set; }
            [JsonPropertyName("category")] public string Category { get; set; }
        }

        // Bu uç eski ajanlar için anahtarsız da kabul edilir (enforce kapalıyken); secret varsa kimlikle gönderilir
        private static void ReportViolation(string domain, string category)
        {
            string hwId, serverUrl;
            lock (Sync) { hwId = _hwId; serverUrl = _serverUrl; }
            if (string.IsNullOrEmpty(hwId) || !POpsHelpers.IsSecureServerUrl(serverUrl)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    string json = JsonSerializer.Serialize(new PolicyAlertPayload { HwId = hwId, Domain = domain, Category = category });
                    using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + "/api/policy_alert")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                    AgentCredentials.AddHttpAuth(request, hwId);
                    using var response = await AgentHttp.Client.SendAsync(request);
                    if (!response.IsSuccessStatusCode) POpsHelpers.Log("TRACKER", $"Kural ihlali bildirilemedi: HTTP {(int)response.StatusCode}.", true);
                }
                catch (Exception ex) { POpsHelpers.Log("TRACKER", $"Kural ihlali bildirilemedi: {ex.Message}", true); }
            });
        }
    }
}
