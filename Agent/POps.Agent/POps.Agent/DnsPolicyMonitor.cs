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
    // Servis komut tüneli ilk kez kurulunca başlatılır (PolicySync.OnCommandChannelConnected); politika her dakika
    // yenilenir (Configure). Eski AdvancedActivityTracker hiç başlatılmıyordu; başlatılsaydı DNS'in yanında
    // hiçbir yere gönderilmeyen dosya/USB/süreç/ağ olaylarını toplayacak ve makinenin uykuya geçmesini
    // engelleyecekti. Yalnızca DNS izleme buraya taşındı.
    //
    // Her Worker'ın kendi örneği (Worker.Dns): otomatik karantina ve DNS hataları yalnızca onu kuran Worker'a gider. Worker
    // atılınca zamanlayıcı durur ve geri çağrılar bırakılır; atılmış bir Worker'a hiçbir şey ulaşmaz.
    [SupportedOSPlatform("windows")]
    public sealed class DnsPolicyMonitor : IDisposable
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan Window = TimeSpan.FromHours(1);
        private const int MaxRemembered = 10000;

        private readonly object _sync = new object();
        private Timer? _timer;
        private AgentPolicy _policy = new AgentPolicy();
        // Açık kategorilerin alan adı dizini; yalnızca kategoriler ya da listeler değişince yeniden kurulur
        private DnsDomainIndex _index = DnsDomainIndex.Empty;
        private string? _indexSignature;
        internal int IndexBuilds { get; private set; }
        private string _hwId = "";
        private string _serverUrl = "";
        // Bu oturumda bildirilen girişler ("kategori|giriş")
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);
        // Pencere içindeki ihlaller (zaman, kategori)
        private readonly List<(DateTime At, string Category)> _recent = new List<(DateTime, string)>();
        // Kullanıcı değiştiğinde önbellekte olan adlar (önceki kullanıcının)
        private HashSet<string> _baseline = new HashSet<string>(StringComparer.Ordinal);
        private bool _quarantined;
        private bool _listMissingLogged;
        private int _checking;

        // Saat, önbellek okuyucu, ihlal bildirimi (testlerde sahteleri), sunucuya bildirimin HTTP istemcisi ve kimlik bilgileri
        private readonly TimeProvider _clock;
        private readonly Func<IEnumerable<string>> _cacheReader;
        private readonly Action<string, string> _reporter;
        private readonly AgentHttp _http;
        private readonly CredentialStore _credentials;
        // Worker'ın geri çağrıları: otomatik karantina (QuarantineControl.LockdownAsync: kilit ekranı + yalıtım + denetim
        // kaydı; neden metniyle) ve DNS hataları. Dispose bırakır.
        private Action<string> _quarantine;
        private Action<string> _errorReporter;

        // quarantine: eşikteki otomatik karantina; errorReporter: DNS hataları (heartbeat'in agent_health'i); http, credentials:
        // /api/policy_alert (bağlamın örnekleri); clock: ihlal penceresinin saati; cacheReader: DNS istemci önbelleği
        // (servis: DnsWatch.ReadCacheNames)
        public DnsPolicyMonitor(Action<string> quarantine, Action<string> errorReporter, AgentHttp http, CredentialStore credentials,
            TimeProvider clock, Func<IEnumerable<string>> cacheReader)
            : this(quarantine, errorReporter, http, credentials, clock, cacheReader, null) { }

        // Testler: ihlal bildirimi sahtedir (null: /api/policy_alert)
        internal DnsPolicyMonitor(Action<string> quarantine, Action<string> errorReporter, AgentHttp http, CredentialStore credentials,
            TimeProvider clock, Func<IEnumerable<string>> cacheReader, Action<string, string>? reporter)
        {
            _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _cacheReader = cacheReader ?? throw new ArgumentNullException(nameof(cacheReader));
            _reporter = reporter ?? ReportViolation;
        }

        private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

        public bool IsRunning { get { lock (_sync) return _timer != null; } }

        // Son 1 saatteki ihlal sayısı
        public int Violations
        {
            get
            {
                lock (_sync)
                {
                    Prune(UtcNow());
                    return _recent.Count;
                }
            }
        }

        // Politika her dakika yeniden okunur; dizin yalnızca dns_categories / dns_domains değiştiyse kurulur
        public void Configure(AgentPolicy? policy, string? hwId, string? serverUrl)
        {
            policy ??= new AgentPolicy();
            string signature = JsonSerializer.Serialize(new object[] { policy.DnsCategories, policy.DnsDomains });
            string? current;
            lock (_sync) current = _indexSignature;
            // Dizin kilit dışında kurulur (büyük listelerde tarama turunu bekletmesin)
            DnsDomainIndex? index = signature != current ? new DnsDomainIndex(policy.DnsCategories, policy.DnsDomains) : null;
            lock (_sync)
            {
                _policy = policy;
                _hwId = hwId ?? "";
                _serverUrl = serverUrl ?? "";
                if (index != null)
                {
                    _index = index;
                    _indexSignature = signature;
                    IndexBuilds++;
                }
            }
        }

        // Birden çok çağrılabilir; yalnızca ilki başlatır
        public void Start()
        {
            lock (_sync)
            {
                if (_timer != null) return;
                _timer = new Timer(_ => CheckNow(), null, Interval, Interval);
            }
            POpsHelpers.Log("TRACKER", "DNS politika izleme başlatıldı.");
        }

        // Worker atılınca: zamanlayıcı durur; sürmekte olan bir tarama turu da atılmış Worker'ın karantinasına ve hata
        // bildirimine ulaşmaz
        public void Dispose()
        {
            lock (_sync)
            {
                _timer?.Dispose();
                _timer = null;
                _quarantine = _ => { };
                _errorReporter = _ => { };
            }
        }

        // Karantina kaldırılınca (unlock ya da çevrimdışı bypass) sayaç sıfırlanır; eşik yeniden aşılırsa yine karantina
        public void ResetViolations()
        {
            lock (_sync)
            {
                _recent.Clear();
                _quarantined = false;
            }
        }

        // Konsoldaki kullanıcı değişti (SessionReporter.UserChanged)
        public void OnUserChanged()
        {
            HashSet<string> present;
            try { present = new HashSet<string>(_cacheReader().Select(DnsWatch.Normalize).Where(n => n != null), StringComparer.Ordinal); }
            catch (Exception ex)
            {
                _errorReporter(ex.Message);
                present = new HashSet<string>(StringComparer.Ordinal);
            }
            lock (_sync)
            {
                _reported.Clear();
                _recent.Clear();
                _baseline = present;
            }
        }

        private void Prune(DateTime now) => _recent.RemoveAll(v => now - v.At >= Window);

        // Bir tarama turu; bu turda bildirilen yeni ihlaller (ilk görülen alan adı, kategori)
        public List<(string Domain, string Category)> CheckNow()
        {
            var found = new List<(string, string)>();
            if (Interlocked.Exchange(ref _checking, 1) == 1) return found;
            try
            {
                AgentPolicy policy;
                DnsDomainIndex index;
                lock (_sync) { policy = _policy; index = _index; }
                if (policy?.DnsCategories == null || policy.DnsCategories.Count == 0) return found;
                if (policy.DnsDomains == null || policy.DnsDomains.Count == 0)
                {
                    if (!_listMissingLogged) POpsHelpers.Log("TRACKER", "DNS kategorileri açık ama politikada alan adı listesi (dns_domains) yok; DNS tespiti yapılmıyor.");
                    _listMissingLogged = true;
                    return found;
                }
                _listMissingLogged = false;
                if (index.Count == 0) return found;

                List<string> names = _cacheReader().Select(DnsWatch.Normalize).Where(n => n != null).Distinct().ToList();
                // Önbellekten düşen ad yeniden görülürse yeni ziyarettir. Okuma boş döndüyse (ya da başarısızsa) taban
                // korunur: yoksa önceki kullanıcının adları bir sonraki okumada yeni kullanıcıya yazılırdı.
                if (names.Count > 0)
                    lock (_sync) _baseline.IntersectWith(names);

                foreach (string domain in names)
                {
                    var match = index.Match(domain);
                    if (match == null) continue;
                    (string category, string entry) = match.Value;

                    string? quarantineReason = null;
                    lock (_sync)
                    {
                        if (_baseline.Contains(domain)) continue;
                        if (_reported.Count >= MaxRemembered) _reported.Clear();
                        if (!_reported.Add(category + "|" + entry)) continue;
                        DateTime now = UtcNow();
                        Prune(now);
                        _recent.Add((now, category));
                        int threshold = Math.Max(1, policy.QuarantineThreshold);
                        if (policy.AutoQuarantine && !_quarantined && _recent.Count >= threshold)
                        {
                            _quarantined = true;
                            quarantineReason = $"{_recent.Count} ihlal / {Window.TotalHours:0} saat ({string.Join(", ", _recent.Select(v => v.Category).Distinct())})";
                        }
                    }

                    found.Add((domain, category));
                    POpsHelpers.Log("TRACKER", $"DNS kural ihlali: {domain} ({category}).");
                    _reporter(domain, category);
                    if (quarantineReason != null)
                    {
                        // Sunucuya ulaşılamasa da eşik uygulanır
                        POpsHelpers.Log("TRACKER", $"Karantina eşiği aşıldı ({quarantineReason}); cihaz karantinaya alınıyor.");
                        _quarantine(quarantineReason);
                    }
                }
            }
            catch (Exception ex)
            {
                _errorReporter(ex.Message);
                POpsHelpers.Log("TRACKER", $"DNS önbelleği okunamadı: {ex.Message}", true);
            }
            finally { Interlocked.Exchange(ref _checking, 0); }
            return found;
        }

        // Sunucu modeli: Backend/pops/models.py PolicyAlertInput
        public sealed class PolicyAlertPayload
        {
            [JsonPropertyName("hw_id")] public string? HwId { get; set; }
            [JsonPropertyName("domain")] public string? Domain { get; set; }
            [JsonPropertyName("category")] public string? Category { get; set; }
        }

        // Bu uç eski ajanlar için anahtarsız da kabul edilir (enforce kapalıyken); secret varsa kimlikle gönderilir
        private void ReportViolation(string domain, string category)
        {
            string hwId, serverUrl;
            lock (_sync) { hwId = _hwId; serverUrl = _serverUrl; }
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
                    _credentials.AddHttpAuth(request, hwId);
                    using var response = await _http.Client.SendAsync(request);
                    if (!response.IsSuccessStatusCode) POpsHelpers.Log("TRACKER", $"Kural ihlali bildirilemedi: HTTP {(int)response.StatusCode}.", true);
                }
                catch (Exception ex)
                {
                    _errorReporter(ex.Message);
                    POpsHelpers.Log("TRACKER", $"Kural ihlali bildirilemedi: {ex.Message}", true);
                }
            });
        }
    }
}
