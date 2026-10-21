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
    public class DnsPolicyMonitorTests : TestBase, IDisposable
    {
        private readonly List<string> _cache = new List<string>();
        private readonly List<(string Domain, string Category)> _reports = new List<(string, string)>();
        private readonly List<string> _quarantines = new List<string>();
        private DateTime _now = new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
        private readonly AgentHarness _agent;
        private readonly DnsPolicyMonitor _dns;

        public DnsPolicyMonitorTests()
        {
            _agent = AgentHarness.Create("dns");
            _dns = _agent.NewDnsMonitor(() => _cache.ToList(), (domain, category) => _reports.Add((domain, category)),
                quarantine: reason => _quarantines.Add(reason), clock: new Clock(() => _now));
        }

        public void Dispose()
        {
            _dns.Dispose();
            _agent.Dispose();
        }

        // Testin saati: her okumada _now
        private sealed class Clock : TimeProvider
        {
            private readonly Func<DateTime> _utcNow;
            public Clock(Func<DateTime> utcNow) => _utcNow = utcNow;
            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(_utcNow(), TimeSpan.Zero);
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
            using var worker = new Worker(NullLogger<Worker>.Instance, _agent.Context, () => _cache.ToList());
            Assert.False(worker.Dns.IsRunning);

            worker.OnCommandChannelConnected();
            Assert.True(worker.Dns.IsRunning);

            // Yeniden bağlanmak ikinci bir zamanlayıcı açmaz
            worker.OnCommandChannelConnected();
            Assert.True(worker.Dns.IsRunning);
        }

        [Fact]
        public void EmptyDomainList_FlagsNothing()
        {
            _cache.AddRange(new[] { "bet1.example", "www.bet1.example", "essex.ac.uk" });

            _dns.Configure(Policy(new Dictionary<string, List<string>>()), "HW-A", "https://pops.example");
            Assert.Empty(_dns.CheckNow());

            _dns.Configure(Policy(null), "HW-A", "https://pops.example");
            Assert.Empty(_dns.CheckNow());

            _dns.Configure(Policy(new Dictionary<string, List<string>> { ["bahis"] = new List<string>() }), "HW-A", "https://pops.example");
            Assert.Empty(_dns.CheckNow());

            Assert.Empty(_reports);
            Assert.Empty(_quarantines);
        }

        // M4: www., cdn., static. aynı liste girişidir: tek ihlal, tek bildirim
        [Fact]
        public void SubdomainsOfOneEntry_CountOnce()
        {
            _cache.AddRange(new[] { "www.bet1.example", "cdn.bet1.example", "static.bet1.example", "essex.ac.uk", "notbet1.example" });
            _dns.Configure(Policy(Lists(), autoQuarantine: true, threshold: 2), "HW-A", "https://pops.example");

            Assert.Single(_dns.CheckNow());
            Assert.Empty(_dns.CheckNow());
            Assert.Single(_reports);
            Assert.Equal(1, _dns.Violations);
            Assert.Empty(_quarantines);
        }

        [Fact]
        public void PolicyUpdate_TakesEffectOnTheNextCheck()
        {
            _cache.Add("bet1.example");
            _dns.Configure(Policy(new Dictionary<string, List<string>>()), "HW-A", "https://pops.example");
            Assert.Empty(_dns.CheckNow());

            _dns.Configure(Policy(Lists()), "HW-A", "https://pops.example");
            Assert.Single(_dns.CheckNow());
        }

        // M4: eşik son 1 saatteki ihlallere uygulanır
        [Fact]
        public void Threshold_UsesAOneHourSlidingWindow()
        {
            _dns.Configure(Policy(Lists(), autoQuarantine: true, threshold: 3), "HW-A", "https://pops.example");

            _cache.Add("bet1.example");
            _dns.CheckNow();
            _now = _now.AddMinutes(40);
            _cache.Add("bet2.example");
            _dns.CheckNow();
            _now = _now.AddMinutes(30);   // ilk ihlal pencereden çıktı
            _cache.Add("bet3.example");
            _dns.CheckNow();
            Assert.Equal(2, _dns.Violations);
            Assert.Empty(_quarantines);

            _now = _now.AddMinutes(10);
            _cache.Add("game.example");
            _dns.CheckNow();
            Assert.Single(_quarantines);
            Assert.Contains("3 ihlal", _quarantines[0]);
            Assert.Contains("bahis", _quarantines[0]);
            Assert.Contains("oyun", _quarantines[0]);
        }

        [Fact]
        public void AutoQuarantine_OnceAtThreshold_AgainAfterReset()
        {
            _dns.Configure(Policy(Lists(), autoQuarantine: true, threshold: 2), "HW-A", "https://pops.example");

            _cache.AddRange(new[] { "bet1.example", "bet2.example", "bet3.example" });
            _dns.CheckNow();
            Assert.Single(_quarantines);

            // unlock / bypass sayacı sıfırlar; eşik yeniden aşılınca yine karantina
            _dns.ResetViolations();
            _cache.AddRange(new[] { "bet4.example", "game.example" });
            _dns.CheckNow();
            Assert.Equal(2, _quarantines.Count);
        }

        // M4: yeni öğrenci önceki öğrencinin ziyaretleriyle karantinaya girmez; aynı siteyi kendisi açınca bildirilir
        [Fact]
        public void UserChange_ResetsCountAndIgnoresThePreviousUsersCache()
        {
            _dns.Configure(Policy(Lists(), autoQuarantine: true, threshold: 2), "HW-A", "https://pops.example");
            _cache.Add("bet1.example");
            _dns.CheckNow();
            Assert.Equal(1, _dns.Violations);

            // Öğrenci değişti; önbellekte öncekinin bet1 ve bet2 kayıtları duruyor
            _cache.Add("bet2.example");
            _dns.OnUserChanged();
            Assert.Equal(0, _dns.Violations);
            Assert.Empty(_dns.CheckNow());
            Assert.Empty(_quarantines);

            // Önceki kayıt önbellekten düştü, yeni öğrenci aynı siteyi açtı: yine bildirilir
            _cache.Clear();
            _dns.CheckNow();
            _cache.Add("www.bet1.example");
            Assert.Equal(new[] { ("www.bet1.example", "bahis") }, _dns.CheckNow());
            Assert.Equal(2, _reports.Count);
            Assert.Equal(1, _dns.Violations);
        }

        [Fact]
        public void NoAutoQuarantine_NeverIsolates()
        {
            _dns.Configure(Policy(Lists(), autoQuarantine: false, threshold: 1), "HW-A", "https://pops.example");
            _cache.AddRange(new[] { "bet1.example", "bet2.example" });
            _dns.CheckNow();
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

    // Machine: LargeLists_CheckQuickly geçen gerçek süreyi ölçer (yük altındaki makinede kayar)
    [Collection(MachineCollection.Name)]
    public class DnsDomainIndexTests : TestBase, IDisposable
    {
        private readonly AgentHarness _agent = AgentHarness.Create("dns-index");

        public void Dispose() => _agent.Dispose();

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
            using DnsPolicyMonitor dns = _agent.NewDnsMonitor(Array.Empty<string>);
            AgentPolicy Policy(params string[] domains) => new AgentPolicy
            {
                DnsCategories = new List<string> { "bahis" },
                DnsDomains = new Dictionary<string, List<string>> { ["bahis"] = domains.ToList() },
            };
            int before = dns.IndexBuilds;
            dns.Configure(Policy("a.example"), "HW-A", "https://pops.example");
            dns.Configure(Policy("a.example"), "HW-A", "https://pops.example");
            dns.Configure(Policy("a.example"), "HW-B", "https://pops.example");
            Assert.Equal(before + 1, dns.IndexBuilds);
            dns.Configure(Policy("a.example", "b.example"), "HW-A", "https://pops.example");
            Assert.Equal(before + 2, dns.IndexBuilds);
        }

        // N1: büyük listelerde tarama turu listenin boyutuyla büyümez (eski yol: ad x giriş normalleştirme)
        [Fact]
        public void LargeLists_CheckQuickly()
        {
            var lists = new Dictionary<string, List<string>>
            {
                ["bahis"] = Enumerable.Range(0, 5000).Select(i => $"bet{i}.örnek-{i % 7}.example").ToList(),
                ["oyun"] = Enumerable.Range(0, 5000).Select(i => $"game{i}.example").ToList(),
            };
            List<string> cache = Enumerable.Range(0, 2000).Select(i => $"host{i}.cdn{i % 13}.innocent{i}.example").ToList();
            using DnsPolicyMonitor dns = _agent.NewDnsMonitor(() => cache, (_, _) => { });
            dns.Configure(new AgentPolicy { DnsCategories = new List<string> { "bahis", "oyun" }, DnsDomains = lists }, "HW-A", "https://pops.example");

            var clock = Stopwatch.StartNew();
            for (int round = 0; round < 20; round++) Assert.Empty(dns.CheckNow());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"20 tur {clock.ElapsedMilliseconds} ms sürdü");
        }

        // M4 notu: önbellek okuması boş/başarısız dönerse önceki kullanıcının taban listesi silinmez
        [Fact]
        public void EmptyCacheRead_KeepsThePreviousUsersBaseline()
        {
            var cache = new List<string> { "bet1.example" };
            var reports = new List<string>();
            using DnsPolicyMonitor dns = _agent.NewDnsMonitor(() => cache.ToList(), (domain, _) => reports.Add(domain));
            dns.Configure(new AgentPolicy
            {
                DnsCategories = new List<string> { "bahis" },
                DnsDomains = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet1.example" } },
            }, "HW-A", "https://pops.example");

            dns.OnUserChanged();                       // bet1 önceki kullanıcının
            cache.Clear();
            dns.CheckNow();                            // okuma boş döndü (ör. DnsGetCacheDataTable başarısız)
            cache.Add("bet1.example");                 // aynı önbellek kaydı yine okundu
            Assert.Empty(dns.CheckNow());
            Assert.Empty(reports);
        }
    }

    // Her Worker'ın kendi DNS izlemesi (Worker.Dns) var, testlerde olduğu gibi Worker birden çok: otomatik karantina ve DNS
    // hataları yalnızca o izlemenin Worker'ına gider. Kurulan Worker başka bir izlemenin geri çağrılarını almaz (eskiden son
    // kurulan Worker hepsini alırdı); atılan Worker hiçbir şey almaz ve başka bir Worker'ı etkilemez. Worker'ların kilidi
    // sahtedir (tepsi listesi, sahte yalıtım), HTTP ve DNS önbelleği sahte.
    public class DnsWorkerBindingTests : TestBase, IDisposable
    {
        // Testin kendi izlemesinin bağlamı (sunucu istekleri sahte işleyiciye)
        private readonly AgentHarness _agent = AgentHarness.Create("dns-workers", new OkHandler());
        private readonly List<string> _cache = new List<string>();
        // true iken önbellek okunamaz (bkz. FailCacheRead)
        private bool _failRead;
        // Hiçbir Worker'a ait olmayan, testin kendi izlemesi ve karantinası
        private readonly List<string> _unbound = new List<string>();
        private readonly DnsPolicyMonitor _dns;

        public DnsWorkerBindingTests()
        {
            _dns = _agent.NewDnsMonitor(Cache, quarantine: _unbound.Add);
            _dns.Configure(Policy(), "HW-DNS", "https://pops.example");
        }

        public void Dispose()
        {
            _dns.Dispose();
            _agent.Dispose();
        }

        private static AgentPolicy Policy() => new AgentPolicy
        {
            DnsCategories = new List<string> { "bahis" },
            DnsDomains = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet1.example", "bet2.example" } },
            AutoQuarantine = true,
            QuarantineThreshold = 1,
        };

        private IEnumerable<string> Cache() => _failRead ? throw new InvalidOperationException("önbellek okunamadı") : _cache.ToList();

        private sealed class OkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        // Kilidi sahte bir Worker: kilit ekranı mesajları Tray'e düşer, yalıtım uygulanmış sayılır; DNS izlemesi testin
        // önbelleğini okur. Her Worker'ın kendi bağlamı (klasörler, sahte sunucu, bellekteki kiosk kaydı)
        private sealed class TestWorker : IDisposable
        {
            public readonly List<string> Tray = new List<string>();
            public readonly Worker Worker;
            private readonly AgentHarness _agent = AgentHarness.Create("dns-worker", new OkHandler());

            public TestWorker(Func<IEnumerable<string>> cache)
            {
                Worker = new Worker(NullLogger<Worker>.Instance, _agent.Context, cache);
                Worker.Quarantine = new QuarantineControl(_agent.Paths, Tray.Add, () => Task.FromResult(true), () => Task.FromResult(true), _agent.Machine.Kiosk);
                Worker.Dns.Configure(Policy(), "HW-DNS", "https://pops.example");
            }

            public int Lockdowns => Tray.Count(m => m.Contains("\"lockdown\"", StringComparison.Ordinal));

            public string LastError => JsonSerializer.SerializeToElement(Worker.HeartbeatPayload())
                .GetProperty("agent_health").GetProperty("last_error").GetString();

            public void Dispose()
            {
                Worker.Dispose();
                _agent.Dispose();
            }
        }

        // Listedeki yeni bir giriş; eşik 1 olduğu için otomatik karantina
        private void Violate(DnsPolicyMonitor dns, string domain)
        {
            _cache.Add(domain);
            dns.ResetViolations();
            Assert.Single(dns.CheckNow());
        }

        // DNS önbelleği okunamadı: hata izlemenin Worker'ının heartbeat'ine (agent_health.last_error) yazılır
        private void FailCacheRead(DnsPolicyMonitor dns)
        {
            _failRead = true;
            try { dns.CheckNow(); }
            finally { _failRead = false; }
        }

        [Fact]
        public void ConstructingAWorker_BindsNothing()
        {
            using var worker = new TestWorker(Cache);
            Violate(_dns, "bet1.example");
            FailCacheRead(_dns);
            Assert.Single(_unbound);
            Assert.Equal(0, worker.Lockdowns);
            Assert.Equal("", worker.LastError);
        }

        // İkinci Worker atıldı: onun izlemesi hiçbir Worker'a karantina ya da hata göndermez. İlki de almaz; o izleme
        // ikincinindir.
        [Fact]
        public void SecondWorkerDisposed_NoWorkerReceivesTheAutoQuarantine()
        {
            using var first = new TestWorker(Cache);
            var second = new TestWorker(Cache);
            second.Dispose();

            Violate(second.Worker.Dns, "bet1.example");
            FailCacheRead(second.Worker.Dns);
            Assert.Equal((0, 0), (first.Lockdowns, second.Lockdowns));
            Assert.Equal(("", ""), (first.LastError, second.LastError));
            Assert.Empty(_unbound);
            Assert.False(File.Exists(first.Worker.Quarantine.LockPath));
        }

        // İlk Worker atıldı, ikincisi çalışıyor: atılan Worker ikinciyi etkilemez; ikincinin izlemesinin karantinası ve hatası
        // ikinciye gider
        [Fact]
        public void FirstWorkerDisposed_TheRunningWorkerStillReceives()
        {
            var first = new TestWorker(Cache);
            using var second = new TestWorker(Cache);
            first.Dispose();

            Violate(second.Worker.Dns, "bet1.example");
            FailCacheRead(second.Worker.Dns);
            Assert.Equal((0, 1), (first.Lockdowns, second.Lockdowns));
            Assert.Equal("", first.LastError);
            Assert.StartsWith("dns: ", second.LastError, StringComparison.Ordinal);
            Assert.Empty(_unbound);

            // Çalışan Worker atılınca o da bırakır
            second.Dispose();
            Violate(second.Worker.Dns, "bet2.example");
            Assert.Equal(1, second.Lockdowns);
            Assert.Empty(_unbound);
        }
    }
}
