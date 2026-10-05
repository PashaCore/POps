using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Karantina: lockdown / unlock / DNS eşiği / çevrimdışı bypass aynı yoldan geçer; kilit durumu diskte tutulur.
    // Tepsi ve güvenlik duvarı sahtedir.
    public class QuarantineControlTests : TestBase
    {
        private const string HwId = "HW-678CC8C5265E";
        private const string Secret = "sekret-Ç-1";
        private static readonly DateTime Day = new DateTime(2026, 9, 26);

        private readonly List<string> _tray = new List<string>();
        private int _enabled, _disabled;
        private bool _disableSucceeds = true;
        private readonly List<LocalAuditEvent> _audit = new List<LocalAuditEvent>();

        public QuarantineControlTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("quarantine");
        }

        private QuarantineControl Control(OfflineBypass bypass = null) => new QuarantineControl(
            _tray.Add,
            () => { _enabled++; File.WriteAllText(NetworkIsolation.StatePath, "{}"); return Task.FromResult(true); },
            () =>
            {
                _disabled++;
                if (_disableSucceeds) File.Delete(NetworkIsolation.StatePath);
                return Task.FromResult(_disableSucceeds);
            },
            bypass ?? new OfflineBypass(), _audit.Add);

        private static string ValidCode() =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HwId + Secret + "2026-09-26"))).Substring(0, 6);

        private static (string Action, string Value) Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string action = doc.RootElement.GetProperty("action").GetString();
            string value = doc.RootElement.TryGetProperty("source", out var s) ? s.GetString()
                : doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return (action, value);
        }

        // F08: kilit ekranı ile ağ yalıtımı ayrı bildirilir; yalıtım uygulanamazsa neden saklanır, tekrar denemede düzelir
        [Fact]
        public async Task FailedIsolation_IsReportedSeparatelyFromTheLockScreen()
        {
            bool isolationWorks = false;
            var control = new QuarantineControl(
                _tray.Add,
                () =>
                {
                    if (isolationWorks) File.WriteAllText(NetworkIsolation.StatePath, "{}");
                    return Task.FromResult(isolationWorks);
                },
                () => Task.FromResult(true),
                new OfflineBypass(), _audit.Add);

            Assert.False(await control.LockdownAsync("Sınav"));
            Assert.True(control.ScreenLocked);
            Assert.False(control.NetworkIsolated);
            Assert.False(string.IsNullOrEmpty(control.LastIsolationError));

            isolationWorks = true;
            Assert.True(await control.LockdownAsync(null));
            Assert.True(control.NetworkIsolated);
            Assert.Null(control.LastIsolationError);
        }

        [Fact]
        public async Task ValidBypassCode_LiftsIsolationThenClosesLockScreen()
        {
            QuarantineControl control = Control();
            await control.LockdownAsync("Sınav");
            _tray.Clear();

            Assert.True(await control.HandleBypassAsync(ValidCode(), HwId, Secret, Day));

            Assert.Equal(("unlock", "bypass"), Parse(_tray[0]));
            Assert.Equal("BYPASS_SUCCESS", _tray[1]);
            Assert.Equal(1, _disabled);
            Assert.False(control.IsLocked);
            Assert.Equal(new[] { 1020, 1021 }, _audit.Select(e => e.EventId));
            Assert.Contains("source: bypass kodu", _audit[1].Message);
        }

        [Fact]
        public async Task WrongCode_ChangesNothing()
        {
            QuarantineControl control = Control();
            await control.LockdownAsync("Sınav");
            _tray.Clear();

            Assert.False(await control.HandleBypassAsync("000000", HwId, Secret, Day));

            Assert.Equal(new[] { "BYPASS_FAILED" }, _tray);
            Assert.Equal(0, _disabled);
            Assert.True(control.IsLocked);
        }

        [Fact]
        public async Task NoBypassSecret_BypassIsDisabled()
        {
            Assert.False(await Control().HandleBypassAsync(ValidCode(), HwId, null, Day));
            Assert.Equal(new[] { "BYPASS_FAILED" }, _tray);
            Assert.Equal(0, _disabled);
        }

        [Fact]
        public async Task ServerUnlock_UsesTheSamePath()
        {
            QuarantineControl control = Control();
            await control.LockdownAsync("Sınav");
            Assert.True(await control.UnlockAsync("server"));
            Assert.Equal(("unlock", "server"), Parse(_tray[^1]));
            Assert.Equal(1, _disabled);
            Assert.False(control.IsLocked);
        }

        // L2: yalıtım kaldırılamazsa kullanıcıya "kaldırıldı" denmez, kilit sürer
        [Fact]
        public async Task FailedIsolationRemoval_KeepsTheLock()
        {
            QuarantineControl control = Control();
            await control.LockdownAsync("Sınav");
            _tray.Clear();
            _disableSucceeds = false;

            Assert.False(await control.UnlockAsync("server"));
            Assert.Equal(new[] { "UNLOCK_FAILED" }, _tray);
            Assert.True(control.IsLocked);

            _tray.Clear();
            Assert.False(await control.HandleBypassAsync(ValidCode(), HwId, Secret, Day));
            Assert.Equal(new[] { "UNLOCK_FAILED" }, _tray);
            Assert.DoesNotContain("BYPASS_SUCCESS", _tray);
        }

        [Fact]
        public async Task StateMachine_LockFailedUnlockThenSuccessfulUnlock()
        {
            QuarantineControl control = Control();
            Assert.False(control.IsLocked);

            Assert.True(await control.LockdownAsync("Sınav"));
            Assert.True(control.IsLocked);
            Assert.Equal(1, _enabled);

            _disableSucceeds = false;
            Assert.False(await control.UnlockAsync("server"));
            Assert.True(control.IsLocked);
            Assert.Equal(1, _disabled);

            _disableSucceeds = true;
            Assert.True(await control.UnlockAsync("server"));
            Assert.False(control.IsLocked);
            Assert.Equal(2, _disabled);
            Assert.Equal(new[] { 1020, 1021 }, _audit.Select(e => e.EventId));
        }

        // M6: kilit ekranı tepsi yeniden bağlanınca (Görev Yöneticisi, oturum kapatma, yeniden başlatma) geri gelir
        [Fact]
        public async Task LockSurvivesTrayReconnectAndServiceRestart()
        {
            QuarantineControl control = Control();
            await control.LockdownAsync("Kural ihlali");

            // Servis yeniden başladı: yeni nesne, durum diskten okunur
            _tray.Clear();
            QuarantineControl afterRestart = Control();
            Assert.True(afterRestart.IsLocked);
            afterRestart.SyncTray();
            Assert.Equal(("lockdown", "Kural ihlali"), Parse(_tray[0]));

            await afterRestart.UnlockAsync("server");
            _tray.Clear();
            afterRestart.SyncTray();
            Assert.Equal(("unlock", "sync"), Parse(_tray[0]));
        }

        [Fact]
        public void IsolationStateAloneMeansLocked()
        {
            // Eski sürümden kalan yalıtım (lockdown.json yok) da kilit sayılır
            File.WriteAllText(NetworkIsolation.StatePath, "{}");
            QuarantineControl control = Control();
            Assert.True(control.IsLocked);
            control.SyncTray();
            Assert.Equal(("lockdown", QuarantineControl.DefaultReason), Parse(_tray[0]));
        }

        [Fact]
        public async Task Unlock_ResetsDnsViolationCount()
        {
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.CacheReader = () => new[] { "x.bet.example" };
            DnsPolicyMonitor.Reporter = (_, _) => { };
            DnsPolicyMonitor.Configure(new AgentPolicy
            {
                DnsCategories = new List<string> { "bahis" },
                DnsDomains = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet.example" } },
            }, HwId, "https://pops.example");
            try
            {
                QuarantineControl control = Control();
                await control.LockdownAsync("DNS kural ihlali eşiği");
                DnsPolicyMonitor.CheckNow();
                Assert.Equal(1, DnsPolicyMonitor.Violations);
                await control.UnlockAsync("bypass");
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
            Assert.True(await Control().LockdownAsync("Sınav"));
            Assert.Equal(("lockdown", "Sınav"), Parse(_tray[0]));
            Assert.Equal(1, _enabled);
            Assert.Equal(("lockdown", "Belirtilmedi"), Parse(QuarantineControl.LockdownMessage(null)));
        }

        // M5: sunucu bu olayları event_type ile tanır (panel karantina durumu, bildirim)
        [Fact]
        public void AuditEvents_MatchWhatTheServerHandles()
        {
            AgentLogPayload auto = QuarantineControl.AutoQuarantineLog("3 ihlal / 1 saat (bahis)");
            Assert.Equal("agent.auto_quarantine", auto.EventType);
            Assert.Equal("auto_quarantine", auto.Action);
            Assert.Equal("Security", auto.LogType);
            Assert.Equal("high", auto.RiskLevel);
            Assert.Equal("3 ihlal / 1 saat (bahis)", auto.Reason);
            Assert.False(string.IsNullOrEmpty(auto.Message));

            AgentLogPayload bypass = QuarantineControl.OfflineBypassLog();
            Assert.Equal("agent.offline_bypass", bypass.EventType);
            Assert.Equal("offline_bypass", bypass.Action);

            Assert.Equal("agent.unlock_failed", QuarantineControl.UnlockFailedLog().EventType);
        }

        // L4: hatalı deneme kilidi servis yeniden başlayınca sıfırlanmaz
        [Fact]
        public void BypassLockout_SurvivesRestart()
        {
            string path = SecureStore.PathOf(OfflineBypass.StateFileName);
            DateTime now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
            var first = new OfflineBypass(() => now, path);
            for (int i = 0; i < OfflineBypass.MaxFailures - 1; i++) first.Attempt("000000", HwId, Secret, Day);
            Assert.Equal(OfflineBypass.Result.LockedOut, first.Attempt("000000", HwId, Secret, Day));

            var afterRestart = new OfflineBypass(() => now.AddMinutes(5), path);
            Assert.Equal(OfflineBypass.Result.Locked, afterRestart.Attempt(ValidCode(), HwId, Secret, Day));

            // Kilit süresi dolunca doğru kod kabul edilir ve sayaçlar sıfırlanır
            var later = new OfflineBypass(() => now.AddMinutes(16), path);
            Assert.Equal(OfflineBypass.Result.Accepted, later.Attempt(ValidCode(), HwId, Secret, Day));
            Assert.Equal(0, new OfflineBypass(() => now.AddMinutes(17), path).Failures);
        }

        [Fact]
        public void BypassFailures_SurviveRestart()
        {
            string path = SecureStore.PathOf(OfflineBypass.StateFileName);
            var first = new OfflineBypass(statePath: path);
            first.Attempt("000000", HwId, Secret, Day);
            first.Attempt("000000", HwId, Secret, Day);
            Assert.Equal(2, new OfflineBypass(statePath: path).Failures);
        }
    }
}
