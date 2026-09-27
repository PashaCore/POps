using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Çevrimdışı bypass kodu sunucunun unlock'u ile aynı yoldan geçer: kilit ekranı kapanır, ağ yalıtımı kalkar.
    // Tepsi ve güvenlik duvarı sahtedir.
    public class QuarantineControlTests : TestBase
    {
        private const string HwId = "HW-678CC8C5265E";
        private const string Secret = "sekret-Ç-1";
        private static readonly DateTime Day = new DateTime(2026, 9, 26);

        private readonly List<string> _tray = new List<string>();
        private int _enabled, _disabled;

        private QuarantineControl Control() => new QuarantineControl(
            _tray.Add,
            () => { _enabled++; return Task.CompletedTask; },
            () => { _disabled++; return Task.CompletedTask; });

        private static string ValidCode() =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HwId + Secret + "2026-09-26"))).Substring(0, 6);

        private static (string Action, string Source) Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return (doc.RootElement.GetProperty("action").GetString(),
                    doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() : null);
        }

        [Fact]
        public async Task ValidBypassCode_ClosesLockScreenAndLiftsIsolation()
        {
            OfflineBypass.Result result = await Control().HandleBypassAsync(ValidCode(), HwId, Secret, Day);

            Assert.Equal(OfflineBypass.Result.Accepted, result);
            Assert.Equal("BYPASS_SUCCESS", _tray[0]);
            Assert.Equal(("unlock", "bypass"), Parse(_tray[1]));
            Assert.Equal(1, _disabled);
            Assert.Equal(0, _enabled);
        }

        [Fact]
        public async Task WrongCode_ChangesNothing()
        {
            OfflineBypass.Result result = await Control().HandleBypassAsync("000000", HwId, Secret, Day);

            Assert.Equal(OfflineBypass.Result.Rejected, result);
            Assert.Equal(new[] { "BYPASS_FAILED" }, _tray);
            Assert.Equal(0, _disabled);
        }

        [Fact]
        public async Task NoBypassSecret_BypassIsDisabled()
        {
            await Control().HandleBypassAsync(ValidCode(), HwId, null, Day);
            Assert.Equal(new[] { "BYPASS_FAILED" }, _tray);
            Assert.Equal(0, _disabled);
        }

        [Fact]
        public async Task ServerUnlock_UsesTheSamePath()
        {
            await Control().UnlockAsync("server");
            Assert.Equal(("unlock", "server"), Parse(_tray[0]));
            Assert.Equal(1, _disabled);
        }

        [Fact]
        public async Task Unlock_ResetsDnsViolationCount()
        {
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.CacheReader = () => new[] { "x.bet.example" };
            DnsPolicyMonitor.Reporter = (_, _) => { };
            DnsPolicyMonitor.Configure(new AgentPolicy
            {
                dns_categories = new List<string> { "bahis" },
                dns_domains = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet.example" } },
            }, HwId, "https://pops.example");
            try
            {
                DnsPolicyMonitor.CheckNow();
                Assert.Equal(1, DnsPolicyMonitor.Violations);
                await Control().UnlockAsync("bypass");
                Assert.Equal(0, DnsPolicyMonitor.Violations);
            }
            finally
            {
                DnsPolicyMonitor.Reset();
                DnsPolicyMonitor.CacheReader = DnsWatch.ReadCacheNames;
            }
        }

        [Fact]
        public async Task Lockdown_ShowsLockScreenAndIsolates()
        {
            await Control().LockdownAsync("Sınav");
            using (JsonDocument doc = JsonDocument.Parse(_tray[0]))
            {
                Assert.Equal("lockdown", doc.RootElement.GetProperty("action").GetString());
                Assert.Equal("Sınav", doc.RootElement.GetProperty("reason").GetString());
            }
            Assert.Equal(1, _enabled);

            using JsonDocument noReason = JsonDocument.Parse(QuarantineControl.LockdownMessage(null));
            Assert.Equal("Belirtilmedi", noReason.RootElement.GetProperty("reason").GetString());
        }
    }
}
