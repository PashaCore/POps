using System;
using System.Collections.Generic;
using System.Linq;
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
            // Worker otomatik karantinayı kendi kilit ekranı yoluna bağlar; testte sahtesine geri alınır
            DnsPolicyMonitor.Quarantine = reason => _quarantines.Add(reason);
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
}
