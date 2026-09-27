using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // DNS izleme artık gerçekten başlatılıyor (F8 kodu vardı ama hiç çağrılmıyordu). Önbellek, bildirim ve karantina
    // sahtedir: gerçek DNS önbelleği okunmaz, sunucuya gidilmez, güvenlik duvarına dokunulmaz.
    public class DnsPolicyMonitorTests : TestBase, IDisposable
    {
        private readonly List<string> _cache = new List<string>();
        private readonly List<(string Domain, string Category)> _reports = new List<(string, string)>();
        private int _quarantines;

        public DnsPolicyMonitorTests()
        {
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.CacheReader = () => _cache;
            DnsPolicyMonitor.Reporter = (domain, category) => _reports.Add((domain, category));
            DnsPolicyMonitor.Quarantine = () => _quarantines++;
        }

        public void Dispose()
        {
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.CacheReader = DnsWatch.ReadCacheNames;
        }

        private static AgentPolicy Policy(Dictionary<string, List<string>> domains, bool autoQuarantine = false, int threshold = 3) => new AgentPolicy
        {
            dns_categories = new List<string> { "bahis" },
            dns_domains = domains,
            auto_quarantine = autoQuarantine,
            quarantine_threshold = threshold,
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
            _cache.AddRange(new[] { "betsite.example", "www.betsite.example", "essex.ac.uk" });

            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>>()), "HW-A", "https://pops.example");
            Assert.Empty(DnsPolicyMonitor.CheckNow());

            DnsPolicyMonitor.Configure(Policy(null), "HW-A", "https://pops.example");
            Assert.Empty(DnsPolicyMonitor.CheckNow());

            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>> { ["bahis"] = new List<string>() }), "HW-A", "https://pops.example");
            Assert.Empty(DnsPolicyMonitor.CheckNow());

            Assert.Empty(_reports);
            Assert.Equal(0, _quarantines);
        }

        [Fact]
        public void ListedDomain_IsReportedOnce()
        {
            _cache.AddRange(new[] { "www.betsite.example", "essex.ac.uk", "notbetsite.example" });
            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "betsite.example" } }), "HW-A", "https://pops.example");

            Assert.Equal(new[] { ("www.betsite.example", "bahis") }, DnsPolicyMonitor.CheckNow());
            Assert.Empty(DnsPolicyMonitor.CheckNow());
            Assert.Equal(new[] { ("www.betsite.example", "bahis") }, _reports);
        }

        [Fact]
        public void PolicyUpdate_TakesEffectOnTheNextCheck()
        {
            _cache.Add("betsite.example");
            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>>()), "HW-A", "https://pops.example");
            Assert.Empty(DnsPolicyMonitor.CheckNow());

            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "betsite.example" } }), "HW-A", "https://pops.example");
            Assert.Single(DnsPolicyMonitor.CheckNow());
        }

        [Fact]
        public void AutoQuarantine_OnceAtThreshold_AgainAfterReset()
        {
            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet.example" } }, autoQuarantine: true, threshold: 2), "HW-A", "https://pops.example");

            _cache.Add("a.bet.example");
            DnsPolicyMonitor.CheckNow();
            Assert.Equal(0, _quarantines);

            _cache.Add("b.bet.example");
            _cache.Add("c.bet.example");
            DnsPolicyMonitor.CheckNow();
            Assert.Equal(1, _quarantines);

            // unlock / bypass sayacı sıfırlar; eşik yeniden aşılınca yine yalıtılır
            DnsPolicyMonitor.ResetViolations();
            _cache.Add("d.bet.example");
            _cache.Add("e.bet.example");
            DnsPolicyMonitor.CheckNow();
            Assert.Equal(2, _quarantines);
        }

        [Fact]
        public void NoAutoQuarantine_NeverIsolates()
        {
            DnsPolicyMonitor.Configure(Policy(new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet.example" } }, autoQuarantine: false, threshold: 1), "HW-A", "https://pops.example");
            _cache.AddRange(new[] { "a.bet.example", "b.bet.example" });
            DnsPolicyMonitor.CheckNow();
            Assert.Equal(2, _reports.Count);
            Assert.Equal(0, _quarantines);
        }
    }
}
