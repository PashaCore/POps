using System;
using System.Collections.Generic;
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
    // ile bildirir, auto_quarantine açıksa eşikte ağı yalıtır. Liste yoksa ya da boşsa hiçbir şey işaretlenmez.
    //
    // Servis komut tüneli ilk kez kurulunca başlatılır (Worker.OnCommandChannelConnected); politika her dakika
    // yenilenir (Configure). Eski AdvancedActivityTracker hiç başlatılmıyordu; başlatılsaydı DNS'in yanında
    // hiçbir yere gönderilmeyen dosya/USB/süreç/ağ olaylarını toplayacak ve makinenin uykuya geçmesini
    // engelleyecekti. Yalnızca DNS izleme buraya taşındı.
    [SupportedOSPlatform("windows")]
    public static class DnsPolicyMonitor
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
        private const int MaxRemembered = 10000;

        private static readonly object Sync = new object();
        private static Timer _timer;
        private static AgentPolicy _policy = new AgentPolicy();
        private static string _hwId = "";
        private static string _serverUrl = "";
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static int _violations;
        private static bool _quarantined;
        private static bool _listMissingLogged;
        private static int _checking;

        // Testlerde değiştirilir: önbellek okuyucu, ihlal bildirimi, karantina
        internal static Func<IEnumerable<string>> CacheReader { get; set; } = DnsWatch.ReadCacheNames;
        internal static Action<string, string> Reporter { get; set; } = ReportViolation;
        internal static Action Quarantine { get; set; } = () => _ = NetworkIsolation.EnableAsync(ServerUrl);

        private static string ServerUrl { get { lock (Sync) return _serverUrl; } }

        public static bool IsRunning { get { lock (Sync) return _timer != null; } }

        public static int Violations { get { lock (Sync) return _violations; } }

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

        // Karantina kaldırılınca (unlock ya da çevrimdışı bypass) sayaç sıfırlanır; eşik yeniden aşılırsa yine yalıtılır
        public static void ResetViolations()
        {
            lock (Sync)
            {
                _violations = 0;
                _quarantined = false;
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
                _violations = 0;
                _quarantined = false;
                _listMissingLogged = false;
            }
        }

        // Bir tarama turu; bu turda bildirilen yeni ihlaller (alan adı, kategori)
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

                foreach (string domain in CacheReader())
                {
                    string category = DnsWatch.MatchCategory(domain, policy.dns_categories, policy.dns_domains);
                    if (category == null) continue;

                    bool quarantineNow;
                    lock (Sync)
                    {
                        if (Reported.Contains(domain)) continue;
                        if (Reported.Count >= MaxRemembered) Reported.Clear();
                        Reported.Add(domain);
                        _violations++;
                        quarantineNow = policy.auto_quarantine && !_quarantined && _violations >= Math.Max(1, policy.quarantine_threshold);
                        if (quarantineNow) _quarantined = true;
                    }

                    found.Add((domain, category));
                    POpsHelpers.Log("TRACKER", $"DNS kural ihlali: {domain} ({category}).");
                    Reporter(domain, category);
                    if (quarantineNow)
                    {
                        // Sunucuya ulaşılamasa da eşik uygulanır
                        POpsHelpers.Log("TRACKER", "Karantina eşiği aşıldı, ağ yalıtılıyor.");
                        Quarantine();
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
