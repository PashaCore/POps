using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // DNS izleme artık gerçekten başlatılıyor (F8 kodu vardı ama hiç çağrılmıyordu). Önbellek, saat, bildirim ve
    // karantina sahtedir: gerçek DNS önbelleği okunmaz, sunucuya gidilmez, güvenlik duvarına dokunulmaz.
    [Collection(SharedStateCollection.Name)]
    public class DnsPolicyMonitorTests : SharedStateTestBase, IDisposable
    {
        private readonly List<string> _cache = new List<string>();
        private readonly List<(string Domain, string Category)> _reports = new List<(string, string)>();
        private readonly List<string> _quarantines = new List<string>();
        private DateTime _now = new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

        public DnsPolicyMonitorTests()
        {
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.UtcNow = () => _now;
            DnsPolicyMonitor.CacheReader = () => _cache.ToList();
            DnsPolicyMonitor.Reporter = (domain, category) => _reports.Add((domain, category));
            DnsPolicyMonitor.Quarantine = reason => _quarantines.Add(reason);
        }

        public void Dispose()
        {
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.UtcNow = () => DateTime.UtcNow;
            DnsPolicyMonitor.CacheReader = DnsWatch.ReadCacheNames;
        }

        private static AgentPolicy Policy(Dictionary<string, List<string>> domains, bool autoQuarantine = false, int threshold = 3) => new AgentPolicy
        {
            DnsCategories = new List<string> { "bahis", "oyun" },
            DnsDomains = domains,
            AutoQuarantine = autoQuarantine,
            QuarantineThreshold = threshold,
        };

        private static Dictionary<string, List<string>> Lists() => new Dictionary<string, List<string>>
        {
            ["bahis"] = new List<string> { "bet1.example", "bet2.example", "bet3.example", "bet4.example" },
            ["oyun"] = new List<string> { "game.example" },
        };

        [Fact]
        public void Worker_StartsMonitoringWhenTheCommandChannelConnects()
        {
            using var worker = new Worker(NullLogger<Worker>.Instance);
            Assert.False(DnsPolicyMonitor.IsRunning);

            worker.OnCommandChannelConnected();
            Assert.True(DnsPolicyMonitor.IsRunning);

            // Yeniden bağlanmak ikinci bir zamanlayıcı açmaz
            worker.OnCommandChannelConnected();
            Assert.True(DnsPolicyMonitor.IsRunning);
        }

        [Fact]
        public void EmptyDomainList_FlagsNothing()
        {
            _cache.AddRange(new[] { "bet1.example", "www.bet1.example", "essex.ac.uk" });

            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>>()), "HW-A", "https://pops.example");
            Assert.Empty(DnsPolicyMonitor.CheckNow());

            DnsPolicyMonitor.Configure(Policy(null), "HW-A", "https://pops.example");
            Assert.Empty(DnsPolicyMonitor.CheckNow());

            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>> { ["bahis"] = new List<string>() }), "HW-A", "https://pops.example");
            Assert.Empty(DnsPolicyMonitor.CheckNow());

            Assert.Empty(_reports);
            Assert.Empty(_quarantines);
        }

        // M4: www., cdn., static. aynı liste girişidir: tek ihlal, tek bildirim
        [Fact]
        public void SubdomainsOfOneEntry_CountOnce()
        {
            _cache.AddRange(new[] { "www.bet1.example", "cdn.bet1.example", "static.bet1.example", "essex.ac.uk", "notbet1.example" });
            DnsPolicyMonitor.Configure(Policy(Lists(), autoQuarantine: true, threshold: 2), "HW-A", "https://pops.example");

            Assert.Single(DnsPolicyMonitor.CheckNow());
            Assert.Empty(DnsPolicyMonitor.CheckNow());
            Assert.Single(_reports);
            Assert.Equal(1, DnsPolicyMonitor.Violations);
            Assert.Empty(_quarantines);
        }

        [Fact]
        public void PolicyUpdate_TakesEffectOnTheNextCheck()
        {
            _cache.Add("bet1.example");
            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>>()), "HW-A", "https://pops.example");
            Assert.Empty(DnsPolicyMonitor.CheckNow());

            DnsPolicyMonitor.Configure(Policy(Lists()), "HW-A", "https://pops.example");
            Assert.Single(DnsPolicyMonitor.CheckNow());
        }

        // M4: eşik son 1 saatteki ihlallere uygulanır
        [Fact]
        public void Threshold_UsesAOneHourSlidingWindow()
        {
            DnsPolicyMonitor.Configure(Policy(Lists(), autoQuarantine: true, threshold: 3), "HW-A", "https://pops.example");

            _cache.Add("bet1.example");
            DnsPolicyMonitor.CheckNow();
            _now = _now.AddMinutes(40);
            _cache.Add("bet2.example");
            DnsPolicyMonitor.CheckNow();
            _now = _now.AddMinutes(30);   // ilk ihlal pencereden çıktı
            _cache.Add("bet3.example");
            DnsPolicyMonitor.CheckNow();
            Assert.Equal(2, DnsPolicyMonitor.Violations);
            Assert.Empty(_quarantines);

            _now = _now.AddMinutes(10);
            _cache.Add("game.example");
            DnsPolicyMonitor.CheckNow();
            Assert.Single(_quarantines);
            Assert.Contains("3 ihlal", _quarantines[0]);
            Assert.Contains("bahis", _quarantines[0]);
            Assert.Contains("oyun", _quarantines[0]);
        }

        [Fact]
        public void AutoQuarantine_OnceAtThreshold_AgainAfterReset()
        {
            DnsPolicyMonitor.Configure(Policy(Lists(), autoQuarantine: true, threshold: 2), "HW-A", "https://pops.example");

            _cache.AddRange(new[] { "bet1.example", "bet2.example", "bet3.example" });
            DnsPolicyMonitor.CheckNow();
            Assert.Single(_quarantines);

            // unlock / bypass sayacı sıfırlar; eşik yeniden aşılınca yine karantina
            DnsPolicyMonitor.ResetViolations();
            _cache.AddRange(new[] { "bet4.example", "game.example" });
            DnsPolicyMonitor.CheckNow();
            Assert.Equal(2, _quarantines.Count);
        }

        // M4: yeni öğrenci önceki öğrencinin ziyaretleriyle karantinaya girmez; aynı siteyi kendisi açınca bildirilir
        [Fact]
        public void UserChange_ResetsCountAndIgnoresThePreviousUsersCache()
        {
            DnsPolicyMonitor.Configure(Policy(Lists(), autoQuarantine: true, threshold: 2), "HW-A", "https://pops.example");
            _cache.Add("bet1.example");
            DnsPolicyMonitor.CheckNow();
            Assert.Equal(1, DnsPolicyMonitor.Violations);

            // Öğrenci değişti; önbellekte öncekinin bet1 ve bet2 kayıtları duruyor
            _cache.Add("bet2.example");
            DnsPolicyMonitor.OnUserChanged();
            Assert.Equal(0, DnsPolicyMonitor.Violations);
            Assert.Empty(DnsPolicyMonitor.CheckNow());
            Assert.Empty(_quarantines);

            // Önceki kayıt önbellekten düştü, yeni öğrenci aynı siteyi açtı: yine bildirilir
            _cache.Clear();
            DnsPolicyMonitor.CheckNow();
            _cache.Add("www.bet1.example");
            Assert.Equal(new[] { ("www.bet1.example", "bahis") }, DnsPolicyMonitor.CheckNow());
            Assert.Equal(2, _reports.Count);
            Assert.Equal(1, DnsPolicyMonitor.Violations);
        }

        [Fact]
        public void NoAutoQuarantine_NeverIsolates()
        {
            DnsPolicyMonitor.Configure(Policy(Lists(), autoQuarantine: false, threshold: 1), "HW-A", "https://pops.example");
            _cache.AddRange(new[] { "bet1.example", "bet2.example" });
            DnsPolicyMonitor.CheckNow();
            Assert.Equal(2, _reports.Count);
            Assert.Empty(_quarantines);
        }

        // L8: okul listeye Türkçe karakterli adı yazmış olabilir; önbellekte punycode durur
        [Fact]
        public void InternationalizedNames_MatchTheirPunycode()
        {
            string puny = new System.Globalization.IdnMapping().GetAscii("bahis-örnek.com");
            Assert.StartsWith("xn--", puny);
            var lists = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bahis-örnek.com" } };

            // Önbellekteki punycode ad, listedeki Unicode girişle eşleşir (ve tersi)
            Assert.Equal("bahis", DnsWatch.MatchCategory("www." + puny, new[] { "bahis" }, lists));
            var punyList = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { puny } };
            Assert.Equal("bahis", DnsWatch.MatchCategory("cdn.bahis-örnek.com", new[] { "bahis" }, punyList));
            Assert.Equal(puny, DnsWatch.Normalize("Bahis-Örnek.com."));
            Assert.Equal(("bahis", puny), DnsWatch.Match("cdn.bahis-örnek.com", new[] { "bahis" }, lists));
            // Benzer görünen başka bir ad eşleşmez
            Assert.Null(DnsWatch.MatchCategory("bahis-ornek.com", new[] { "bahis" }, lists));
        }
    }

    [Collection(SharedStateCollection.Name)]
    public class DnsDomainIndexTests : SharedStateTestBase, IDisposable
    {
        public void Dispose()
        {
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.UtcNow = () => DateTime.UtcNow;
            DnsPolicyMonitor.CacheReader = DnsWatch.ReadCacheNames;
        }

        [Fact]
        public void SuffixChain_FindsTheEntryAndRespectsCategoryOrder()
        {
            var lists = new Dictionary<string, List<string>>
            {
                ["oyun"] = new List<string> { "cdn.site.example" },
                ["bahis"] = new List<string> { "SITE.example.", "other.example" },
            };
            var index = new DnsDomainIndex(new[] { "bahis", "oyun" }, lists);
            Assert.Equal(3, index.Count);
            Assert.Equal(("bahis", "site.example"), index.Match("www.site.example"));
            // Hem cdn.site.example (oyun) hem site.example (bahis) uyar: politikadaki sıra (bahis önce) belirler
            Assert.Equal(("bahis", "site.example"), index.Match("x.cdn.site.example"));
            Assert.Null(index.Match("notsite.example"));
            Assert.Null(index.Match("example"));
            Assert.Null(DnsDomainIndex.Empty.Match("site.example"));
        }

        // N1: dizin yalnızca listeler değişince kurulur (politika her dakika yeniden okunur)
        [Fact]
        public void Index_IsBuiltOnlyWhenListsChange()
        {
            DnsPolicyMonitor.Reset();
            AgentPolicy Policy(params string[] domains) => new AgentPolicy
            {
                DnsCategories = new List<string> { "bahis" },
                DnsDomains = new Dictionary<string, List<string>> { ["bahis"] = domains.ToList() },
            };
            int before = DnsPolicyMonitor.IndexBuilds;
            DnsPolicyMonitor.Configure(Policy("a.example"), "HW-A", "https://pops.example");
            DnsPolicyMonitor.Configure(Policy("a.example"), "HW-A", "https://pops.example");
            DnsPolicyMonitor.Configure(Policy("a.example"), "HW-B", "https://pops.example");
            Assert.Equal(before + 1, DnsPolicyMonitor.IndexBuilds);
            DnsPolicyMonitor.Configure(Policy("a.example", "b.example"), "HW-A", "https://pops.example");
            Assert.Equal(before + 2, DnsPolicyMonitor.IndexBuilds);
        }

        // N1: büyük listelerde tarama turu listenin boyutuyla büyümez (eski yol: ad x giriş normalleştirme)
        [Fact]
        public void LargeLists_CheckQuickly()
        {
            DnsPolicyMonitor.Reset();
            var lists = new Dictionary<string, List<string>>
            {
                ["bahis"] = Enumerable.Range(0, 5000).Select(i => $"bet{i}.örnek-{i % 7}.example").ToList(),
                ["oyun"] = Enumerable.Range(0, 5000).Select(i => $"game{i}.example").ToList(),
            };
            List<string> cache = Enumerable.Range(0, 2000).Select(i => $"host{i}.cdn{i % 13}.innocent{i}.example").ToList();
            DnsPolicyMonitor.CacheReader = () => cache;
            DnsPolicyMonitor.Reporter = (_, _) => { };
            DnsPolicyMonitor.Configure(new AgentPolicy { DnsCategories = new List<string> { "bahis", "oyun" }, DnsDomains = lists }, "HW-A", "https://pops.example");

            var clock = Stopwatch.StartNew();
            for (int round = 0; round < 20; round++) Assert.Empty(DnsPolicyMonitor.CheckNow());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"20 tur {clock.ElapsedMilliseconds} ms sürdü");
        }

        // M4 notu: önbellek okuması boş/başarısız dönerse önceki kullanıcının taban listesi silinmez
        [Fact]
        public void EmptyCacheRead_KeepsThePreviousUsersBaseline()
        {
            DnsPolicyMonitor.Reset();
            var cache = new List<string> { "bet1.example" };
            DnsPolicyMonitor.CacheReader = () => cache.ToList();
            var reports = new List<string>();
            DnsPolicyMonitor.Reporter = (domain, _) => reports.Add(domain);
            DnsPolicyMonitor.Configure(new AgentPolicy
            {
                DnsCategories = new List<string> { "bahis" },
                DnsDomains = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet1.example" } },
            }, "HW-A", "https://pops.example");

            DnsPolicyMonitor.OnUserChanged();          // bet1 önceki kullanıcının
            cache.Clear();
            DnsPolicyMonitor.CheckNow();               // okuma boş döndü (ör. DnsGetCacheDataTable başarısız)
            cache.Add("bet1.example");                 // aynı önbellek kaydı yine okundu
            Assert.Empty(DnsPolicyMonitor.CheckNow());
            Assert.Empty(reports);
        }
    }

    // İzleme statik, Worker birden çok (testlerde olduğu gibi): otomatik karantina ve DNS hataları yalnızca çalışan
    // Worker'a gider. Kurulan Worker bağlamaz (eskiden son kurulan Worker hepsini alırdı); atılan Worker hiçbir şey almaz
    // ve kendinden sonra bağlanan Worker'ı çözmez. Worker'ların kilidi sahtedir (tepsi listesi, sahte yalıtım), HTTP sahte.
    [Collection(SharedStateCollection.Name)]
    public class DnsWorkerBindingTests : SharedStateTestBase, IDisposable
    {
        // Testten önceki değerler (temel kurucudan sonra okunur: alan başlatıcıları ortam kurulmadan çalışır)
        private readonly string _secureDir;
        private readonly HttpClient _client;
        private readonly POps.Shared.IKioskRegistry _registry;
        private readonly Func<IEnumerable<string>> _cacheReader;
        private readonly Action<string, string> _reporter;
        private readonly Action<string> _quarantine;
        private readonly List<string> _cache = new List<string>();
        // Hiçbir Worker bağlı değilken testin kendi karantinası
        private readonly List<string> _unbound = new List<string>();

        public DnsWorkerBindingTests()
        {
            (_secureDir, _client, _registry) = (SecureStore.Dir, AgentHttp.Client, KioskMode.Registry);
            (_cacheReader, _reporter, _quarantine) = (DnsPolicyMonitor.CacheReader, DnsPolicyMonitor.Reporter, DnsPolicyMonitor.Quarantine);
            SecureStore.Dir = TestEnvironment.NewDir("dns-workers");
            AgentHttp.Client = new HttpClient(new OkHandler());
            KioskMode.Registry = new FakeKioskRegistry();
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.CacheReader = () => _cache.ToList();
            DnsPolicyMonitor.Reporter = (_, _) => { };
            DnsPolicyMonitor.Quarantine = _unbound.Add;
            DnsPolicyMonitor.Configure(new AgentPolicy
            {
                DnsCategories = new List<string> { "bahis" },
                DnsDomains = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet1.example", "bet2.example" } },
                AutoQuarantine = true,
                QuarantineThreshold = 1,
            }, "HW-DNS", "https://pops.example");
        }

        public void Dispose()
        {
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.CacheReader = _cacheReader;
            DnsPolicyMonitor.Reporter = _reporter;
            DnsPolicyMonitor.Quarantine = _quarantine;
            KioskMode.Registry = _registry;
            AgentHttp.Client = _client;
            SecureStore.Dir = _secureDir;
        }

        private sealed class OkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        // Kilidi sahte bir Worker: kilit ekranı mesajları Tray'e düşer, yalıtım uygulanmış sayılır
        private sealed class TestWorker : IDisposable
        {
            public readonly List<string> Tray = new List<string>();
            public readonly Worker Worker = new Worker(NullLogger<Worker>.Instance);

            public TestWorker() => Worker.Quarantine = new QuarantineControl(Tray.Add, () => Task.FromResult(true), () => Task.FromResult(true));

            public int Lockdowns => Tray.Count(m => m.Contains("\"lockdown\"", StringComparison.Ordinal));

            public string LastError => JsonSerializer.SerializeToElement(Worker.HeartbeatPayload())
                .GetProperty("agent_health").GetProperty("last_error").GetString();

            public void Dispose() => Worker.Dispose();
        }

        // Listedeki yeni bir giriş; eşik 1 olduğu için otomatik karantina
        private void Violate(string domain)
        {
            _cache.Add(domain);
            DnsPolicyMonitor.ResetViolations();
            Assert.Single(DnsPolicyMonitor.CheckNow());
        }

        // DNS önbelleği okunamadı: hata bağlı Worker'ın heartbeat'ine (agent_health.last_error) yazılır
        private void FailCacheRead()
        {
            DnsPolicyMonitor.CacheReader = () => throw new InvalidOperationException("önbellek okunamadı");
            try { DnsPolicyMonitor.CheckNow(); }
            finally { DnsPolicyMonitor.CacheReader = () => _cache.ToList(); }
        }

        [Fact]
        public void ConstructingAWorker_BindsNothing()
        {
            using var worker = new TestWorker();
            Violate("bet1.example");
            FailCacheRead();
            Assert.Single(_unbound);
            Assert.Equal(0, worker.Lockdowns);
            Assert.Equal("", worker.LastError);
        }

        // İkinci Worker en son bağlandı ve atıldı: karantina hiçbir Worker'a gitmez. İlki de almaz; onun bağı ikincinin
        // bağlanmasıyla bitti.
        [Fact]
        public void SecondWorkerDisposed_NoWorkerReceivesTheAutoQuarantine()
        {
            using var first = new TestWorker();
            var second = new TestWorker();
            first.Worker.BindDnsPolicyMonitor();
            second.Worker.BindDnsPolicyMonitor();
            second.Dispose();

            Violate("bet1.example");
            FailCacheRead();
            Assert.Equal((0, 0), (first.Lockdowns, second.Lockdowns));
            Assert.Equal(("", ""), (first.LastError, second.LastError));
            Assert.Empty(_unbound);
            Assert.False(File.Exists(QuarantineControl.LockPath));
        }

        // İlk Worker atıldı, ikincisi çalışıyor: atılan Worker ikincinin bağını çözmez; karantina ve hata ikinciye gider
        [Fact]
        public void FirstWorkerDisposed_TheRunningWorkerStillReceives()
        {
            var first = new TestWorker();
            using var second = new TestWorker();
            first.Worker.BindDnsPolicyMonitor();
            second.Worker.BindDnsPolicyMonitor();
            first.Dispose();

            Violate("bet1.example");
            FailCacheRead();
            Assert.Equal((0, 1), (first.Lockdowns, second.Lockdowns));
            Assert.Equal("", first.LastError);
            Assert.StartsWith("dns: ", second.LastError, StringComparison.Ordinal);
            Assert.Empty(_unbound);

            // Çalışan Worker atılınca o da bırakır
            second.Dispose();
            Violate("bet2.example");
            Assert.Equal(1, second.Lockdowns);
            Assert.Empty(_unbound);
        }
    }
}
