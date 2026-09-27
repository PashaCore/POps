using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // PR #23 yeniden inceleme: WUA işinin temizlenmesi (M1), DNS dizini (N1), heartbeat'te karantina ve idempotent
    // kilit/açma (HB), boş önbellek okuması (M4 notu), bypass sayaçlarının unutulması (L4 notu)

    // WUA iş nesnesinin sahtesi (dynamic ile çağrılır; gerçek COM yok)
    public sealed class FakeWuaJob
    {
        public bool IsCompleted { get; set; }
        public int Aborts;
        public int CleanUps;
        public int CleanUpThread;
        public readonly ManualResetEventSlim AllowCleanUp = new ManualResetEventSlim(true);
        public void RequestAbort() => Interlocked.Increment(ref Aborts);
        public void CleanUp()
        {
            CleanUpThread = Environment.CurrentManagedThreadId;
            AllowCleanUp.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref CleanUps);
        }
    }

    public sealed class ThrowingWuaJob
    {
        public int CleanUps;
        public bool IsCompleted => throw new InvalidOperationException("RPC sunucusu yok");
        public void CleanUp() => CleanUps++;
    }

    public class WuaJobTests : TestBase
    {
        [Fact]
        public void HungJob_IsAbortedAndNotWaitedForever()
        {
            TimeSpan previous = WindowsUpdateAgent.AbortGrace;
            WindowsUpdateAgent.AbortGrace = TimeSpan.FromMilliseconds(300);
            try
            {
                var job = new FakeWuaJob { IsCompleted = false };
                var clock = Stopwatch.StartNew();
                Assert.True(WindowsUpdateAgent.WaitForJob(job, TimeSpan.FromMilliseconds(100)));
                Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
                Assert.Equal(1, job.Aborts);
            }
            finally { WindowsUpdateAgent.AbortGrace = previous; }
        }

        [Fact]
        public void FinishedJob_IsNotAborted()
        {
            var job = new FakeWuaJob { IsCompleted = true };
            Assert.False(WindowsUpdateAgent.WaitForJob(job, TimeSpan.FromMinutes(1)));
            Assert.Equal(0, job.Aborts);
        }

        // M1: CleanUp işin bitmesini bekler; bitmemiş işte çağıran thread bloklanmamalı
        [Fact]
        public void UnfinishedJob_IsCleanedUpOnADetachedThread()
        {
            var job = new FakeWuaJob { IsCompleted = false };
            job.AllowCleanUp.Reset();   // CleanUp "iş bitene kadar" takılı kalır

            var clock = Stopwatch.StartNew();
            WindowsUpdateAgent.ReleaseJob(job);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), "ReleaseJob bitmemiş işi beklememeli");

            job.AllowCleanUp.Set();
            SpinWait.SpinUntil(() => Volatile.Read(ref job.CleanUps) == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, job.CleanUps);
            Assert.NotEqual(Environment.CurrentManagedThreadId, job.CleanUpThread);
        }

        [Fact]
        public void FinishedJob_IsCleanedUpRightAway()
        {
            var job = new FakeWuaJob { IsCompleted = true };
            WindowsUpdateAgent.ReleaseJob(job);
            Assert.Equal(1, job.CleanUps);
            Assert.Equal(Environment.CurrentManagedThreadId, job.CleanUpThread);
        }

        [Fact]
        public void UnreadableJob_IsLeftAlone()
        {
            var job = new ThrowingWuaJob();
            WindowsUpdateAgent.ReleaseJob(job);
            Assert.Equal(0, job.CleanUps);
        }
    }

    public class DnsDomainIndexTests : TestBase, IDisposable
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
                dns_categories = new List<string> { "bahis" },
                dns_domains = new Dictionary<string, List<string>> { ["bahis"] = domains.ToList() },
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
            DnsPolicyMonitor.Configure(new AgentPolicy { dns_categories = new List<string> { "bahis", "oyun" }, dns_domains = lists }, "HW-A", "https://pops.example");

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
                dns_categories = new List<string> { "bahis" },
                dns_domains = new Dictionary<string, List<string>> { ["bahis"] = new List<string> { "bet1.example" } },
            }, "HW-A", "https://pops.example");

            DnsPolicyMonitor.OnUserChanged();          // bet1 önceki kullanıcının
            cache.Clear();
            DnsPolicyMonitor.CheckNow();               // okuma boş döndü (ör. DnsGetCacheDataTable başarısız)
            cache.Add("bet1.example");                 // aynı önbellek kaydı yine okundu
            Assert.Empty(DnsPolicyMonitor.CheckNow());
            Assert.Empty(reports);
        }
    }

    // HB: heartbeat karantina durumunu taşır; kilit/açma idempotenttir
    public class QuarantineHeartbeatTests : TestBase, IDisposable
    {
        private readonly List<string> _tray = new List<string>();
        private int _enabled, _disabled;

        public QuarantineHeartbeatTests() => SecureStore.Dir = TestEnvironment.NewDir("hb");

        public void Dispose() => DnsPolicyMonitor.Quarantine = _ => { };

        private QuarantineControl Control() => new QuarantineControl(
            _tray.Add,
            () => { _enabled++; File.WriteAllText(NetworkIsolation.StatePath, "{}"); return Task.FromResult(true); },
            () => { _disabled++; File.Delete(NetworkIsolation.StatePath); return Task.FromResult(true); },
            new OfflineBypass());

        private static JsonElement Heartbeat(Worker worker) =>
            JsonDocument.Parse(JsonSerializer.Serialize(worker.HeartbeatPayload())).RootElement;

        [Fact]
        public void Heartbeat_ReportsTheLockState()
        {
            using var worker = new Worker(NullLogger<Worker>.Instance);
            Assert.Equal(JsonValueKind.False, Heartbeat(worker).GetProperty("quarantined").ValueKind);

            File.WriteAllText(QuarantineControl.LockPath, "{\"reason\":\"Sınav\"}");
            JsonElement hb = Heartbeat(worker);
            Assert.Equal(JsonValueKind.True, hb.GetProperty("quarantined").ValueKind);
            Assert.Equal("Online", hb.GetProperty("status").GetString());
            Assert.True(hb.TryGetProperty("active_window", out _));
        }

        [Fact]
        public async Task RepeatedLockdown_DoesNotRebuildIsolation()
        {
            QuarantineControl control = Control();
            await control.LockdownAsync("Sınav");
            Assert.True(await control.LockdownAsync("Sınav"));
            // Sunucunun yeniden gönderdiği nedensiz kilit kayıtlı nedeni silmez
            await control.LockdownAsync(null);
            Assert.Equal(1, _enabled);
            Assert.Equal("Sınav", control.LockReason);
            Assert.True(control.IsLocked);
        }

        [Fact]
        public async Task RepeatedUnlock_IsQuiet()
        {
            QuarantineControl control = Control();
            await control.LockdownAsync("Sınav");
            Assert.True(await control.UnlockAsync("server"));
            _tray.Clear();

            Assert.True(await control.UnlockAsync("server"));
            Assert.Equal(1, _disabled);
            using JsonDocument doc = JsonDocument.Parse(_tray.Single());
            Assert.Equal("sync", doc.RootElement.GetProperty("source").GetString());
        }
    }

    // L4 notu: 24 saat hatasız geçince eski hatalar ve kilitlenmeler unutulur
    public class BypassDecayTests : TestBase
    {
        private const string HwId = "HW-678CC8C5265E", Secret = "sekret-Ç-1";
        private static readonly DateTime Day = new DateTime(2026, 9, 26);

        public BypassDecayTests() => SecureStore.Dir = TestEnvironment.NewDir("decay");

        [Fact]
        public void OldLockouts_AreForgottenAfterADayWithoutFailures()
        {
            string path = SecureStore.PathOf(OfflineBypass.StateFileName);
            DateTime t = new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc);
            DateTime now = t;
            var guard = new OfflineBypass(() => now, path);

            for (int lockout = 0; lockout < 3; lockout++)
            {
                for (int i = 0; i < OfflineBypass.MaxFailures; i++) guard.Attempt("000000", HwId, Secret, Day);
                now = guard.LockedUntilUtc;   // kilit bitti
            }
            Assert.Equal(3, guard.Lockouts);

            // Bir gün hatasız: bir sonraki kilit yine 15 dakika (60 değil)
            now = now.AddHours(25);
            var afterRestart = new OfflineBypass(() => now, path);
            for (int i = 0; i < OfflineBypass.MaxFailures; i++) afterRestart.Attempt("000000", HwId, Secret, Day);
            Assert.Equal(now + TimeSpan.FromMinutes(15), afterRestart.LockedUntilUtc);
            Assert.Equal(1, afterRestart.Lockouts);
        }

        [Fact]
        public void RecentFailures_AreNotForgotten()
        {
            DateTime now = new DateTime(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc);
            var guard = new OfflineBypass(() => now);
            guard.Attempt("000000", HwId, Secret, Day);
            guard.Attempt("000000", HwId, Secret, Day);
            now = now.AddHours(23);
            guard.Attempt("000000", HwId, Secret, Day);
            Assert.Equal(3, guard.Failures);
        }
    }
}
