using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Windows Update sınıflandırması ve sunucuya giden durum. WUA'ya dokunulmaz: güncellemeler elle kurulur.
    public class PatchStatusTests : TestBase
    {
        private const string UpdatesId = "cd5ffd1e-e932-4e3a-bf74-18bf0b1bbd83";
        private const string DefinitionUpdatesId = "e0789628-ce08-4437-be74-2495b842f43b";

        private static PendingUpdate Update(string kb, string severity = null, params (string Name, string Id)[] categories) => new PendingUpdate
        {
            Kb = kb,
            Title = "Güncelleştirme " + kb,
            Severity = severity,
            Categories = categories.Select(c => c.Name).ToList(),
            CategoryIds = categories.Select(c => c.Id).ToList(),
        };

        private static readonly (string, string) Security = ("Security Updates", PatchClassifier.SecurityUpdatesId);
        private static readonly (string, string) Critical = ("Critical Updates", PatchClassifier.CriticalUpdatesId);
        private static readonly (string, string) Plain = ("Updates", UpdatesId);
        private static readonly (string, string) Definition = ("Definition Updates", DefinitionUpdatesId);
        private static readonly (string, string) Product = ("Windows 10, version 1903 and later", "b3c75dc1-155f-4be4-b015-3f1a91758e52");

        [Fact]
        public void SecurityCategory_IsSecurity_EvenWithLocalizedName()
        {
            Assert.True(Update("KB1", null, Security).IsSecurity);
            // Kategori adı Windows diline göre değişebilir; kimlik belirleyicidir (süslü parantez / büyük harf de olur)
            Assert.True(Update("KB2", null, ("Güvenlik Güncelleştirmeleri", "{0FA1201D-4330-4FA8-8AE9-B877473B6441}")).IsSecurity);
            // Kimlik gelmezse İngilizce ad yedektir
            Assert.True(Update("KB3", null, ("Security Updates", null)).IsSecurity);
        }

        [Fact]
        public void MsrcSeverity_MakesAnUpdateSecurity()
        {
            Assert.True(Update("KB1", "Important", Plain).IsSecurity);
            Assert.True(Update("KB2", "Low").IsSecurity);
        }

        [Fact]
        public void OrdinaryAndDefinitionUpdates_AreNotSecurity()
        {
            Assert.False(Update("KB1", null, Plain, Product).IsSecurity);
            Assert.False(Update("KB2", null, Definition).IsSecurity);
            Assert.False(Update("KB3", "  ").IsSecurity);
        }

        [Fact]
        public void Critical_ByMsrcSeverityOrCategory()
        {
            Assert.True(Update("KB1", "Critical", Security).IsCritical);
            Assert.True(Update("KB2", "critical").IsCritical);
            Assert.True(Update("KB3", null, Critical).IsCritical);
            Assert.False(Update("KB4", "Important", Security).IsCritical);
            // Güvenlik dışı "Critical Updates" kritik sayılır ama güvenlik değildir
            Assert.False(Update("KB5", null, Critical).IsSecurity);
        }

        [Fact]
        public void BuildStatus_CountsAndFields()
        {
            var pending = new List<PendingUpdate>
            {
                Update("KB100", null, Plain),
                Update("KB200", "Critical", Security),
                Update("KB300", "Important", Security),
                Update("KB400", null, Critical),
                Update(null, null, Definition),
            };
            DateTime searched = new DateTime(2026, 9, 27, 12, 51, 23, DateTimeKind.Utc);
            DateTime installed = new DateTime(2026, 9, 27, 11, 31, 27, DateTimeKind.Utc);

            PatchStatusPayload status = PatchClassifier.BuildStatus(pending, true, searched, installed, null);

            Assert.Equal(5, status.PendingCount);
            Assert.Equal(2, status.PendingSecurity);
            Assert.Equal(2, status.PendingCritical);
            Assert.True(status.RebootRequired);
            Assert.Equal("2026-09-27T12:51:23Z", status.LastSearch);
            Assert.Equal("2026-09-27T11:31:27Z", status.LastInstall);
            Assert.Null(status.LastResult);
            // Önce kritik, sonra güvenlik
            Assert.Equal(new[] { "KB200", "KB400", "KB300" }, status.Updates.Take(3).Select(u => u.Kb));
            PatchUpdateItem kb200 = status.Updates.Single(u => u.Kb == "KB200");
            Assert.Equal("Critical", kb200.Severity);
            Assert.True(kb200.IsSecurity);
            Assert.Equal(new[] { "Security Updates" }, kb200.Categories);
            Assert.Null(status.Updates.Single(u => u.Kb == "KB100").Severity);
        }

        [Fact]
        public void BuildStatus_ListCappedAt500_CountsCoverAll()
        {
            var pending = Enumerable.Range(0, 520).Select(i => Update($"KB{i}", i < 10 ? "Important" : null, i < 10 ? Security : Plain)).ToList();
            PatchStatusPayload status = PatchClassifier.BuildStatus(pending, false, DateTime.UtcNow, null, null);
            Assert.Equal(520, status.PendingCount);
            Assert.Equal(10, status.PendingSecurity);
            Assert.Equal(PatchClassifier.MaxUpdates, status.Updates.Count);
            // Kesilen kısım güvenlik güncellemelerini kaybetmez
            Assert.Equal(10, status.Updates.Count(u => u.IsSecurity));
            Assert.Null(status.LastInstall);
        }

        // L5: "security" kapsamı güvenlik VE kritik güncellemeleri kurar (panel ve belgeler "güvenlik/kritik" der)
        [Fact]
        public void SelectForInstall_ByScope()
        {
            var pending = new List<PendingUpdate> { Update("KB1", null, Plain), Update("KB2", "Important", Security), Update("KB3", null, Critical), Update("KB4", null, Security) };
            Assert.Equal(new[] { 1, 2, 3 }, PatchClassifier.SelectForInstall(pending, "security"));
            Assert.Equal(new[] { 0, 1, 2, 3 }, PatchClassifier.SelectForInstall(pending, "all"));
            Assert.Empty(PatchClassifier.SelectForInstall(pending, "drivers"));
            Assert.Empty(PatchClassifier.SelectForInstall(pending, null));
        }

        private static readonly (string, string) Upgrades = ("Upgrades", "{3689BDC8-B205-4AF4-8D4A-A63924C5E9D5}");

        // M3: isteğe bağlı/önizleme (BrowseOnly) ve sürüm yükseltmeleri (Upgrades) hiçbir kapsamda kurulmaz ve
        // bekleyen sayılmaz
        [Fact]
        public void OptionalAndUpgradeUpdates_AreNeverInstalledOrCounted()
        {
            PendingUpdate preview = Update("KB10", null, Plain);
            preview.BrowseOnly = true;
            PendingUpdate securityPreview = Update("KB11", "Important", Security);
            securityPreview.BrowseOnly = true;
            PendingUpdate featureUpgrade = Update(null, null, Upgrades, Product);
            var pending = new List<PendingUpdate> { preview, securityPreview, featureUpgrade, Update("KB12", "Critical", Security) };

            Assert.False(PatchClassifier.IsManaged(preview));
            Assert.False(PatchClassifier.IsManaged(featureUpgrade));
            Assert.True(PatchClassifier.IsUpgrade(featureUpgrade));
            Assert.Equal(new[] { 3 }, PatchClassifier.SelectForInstall(pending, "all"));
            Assert.Equal(new[] { 3 }, PatchClassifier.SelectForInstall(pending, "security"));

            PatchStatusPayload status = PatchClassifier.BuildStatus(pending, false, DateTime.UtcNow, null, null);
            Assert.Equal(1, status.PendingCount);
            Assert.Equal(1, status.PendingSecurity);
            Assert.Equal(new[] { "KB12" }, status.Updates.Select(u => u.Kb));
        }

        // L11: alanlar ajanda da sunucunun sütun sınırlarına kısaltılır
        [Fact]
        public void LongFields_AreClipped()
        {
            var update = new PendingUpdate
            {
                Kb = "KB" + new string('9', 40),
                Title = new string('t', 400),
                Severity = new string('s', 30),
                Categories = Enumerable.Range(0, 15).Select(i => new string((char)('a' + i), 80)).ToList(),
            };
            PatchUpdateItem item = PatchClassifier.BuildStatus(new[] { update }, false, DateTime.UtcNow, null, null).Updates.Single();
            Assert.Equal(PatchClassifier.MaxKb, item.Kb.Length);
            Assert.Equal(PatchClassifier.MaxTitle, item.Title.Length);
            Assert.Equal(PatchClassifier.MaxSeverity, item.Severity.Length);
            Assert.Equal(PatchClassifier.MaxCategories, item.Categories.Count);
            Assert.All(item.Categories, c => Assert.Equal(PatchClassifier.MaxCategory, c.Length));
        }

        [Fact]
        public void Summary_ReportsATimeout()
        {
            Assert.Equal("1 güncelleme kuruldu, 2 başarısız (KB1, KB2); süre doldu, kurulum durduruldu",
                PatchClassifier.Summarize(1, new List<string> { "KB1", "KB2" }, 0, false, timedOut: true));
            Assert.Equal("0 güncelleme kuruldu; süre doldu, kurulum durduruldu; yeniden başlatma gerekiyor",
                PatchClassifier.Summarize(0, null, 0, true, timedOut: true));
        }

        [Theory]
        [InlineData(3, "KB5043080", 0, false, "3 güncelleme kuruldu, 1 başarısız (KB5043080)")]
        [InlineData(2, null, 0, true, "2 güncelleme kuruldu; yeniden başlatma gerekiyor")]
        [InlineData(0, "KB1,KB2", 1, false, "0 güncelleme kuruldu, 2 başarısız (KB1, KB2), 1 atlandı (kullanıcı girişi istiyor)")]
        [InlineData(0, null, 0, false, "Kurulacak güncelleme yok")]
        public void Summary_IsShortTurkish(int installed, string failed, int skipped, bool reboot, string expected)
        {
            var failedList = failed == null ? new List<string>() : failed.Split(',').ToList();
            Assert.Equal(expected, PatchClassifier.Summarize(installed, failedList, skipped, reboot));
        }

        [Fact]
        public void Summary_FitsServerColumn()
        {
            var failed = Enumerable.Range(0, 200).Select(i => $"KB{5000000 + i}").ToList();
            Assert.True(PatchClassifier.Summarize(1, failed, 0, true).Length <= 500);
        }

        [Fact]
        public void Label_UsesKbOrShortTitle()
        {
            Assert.Equal("KB5043080", PatchClassifier.Label(Update("KB5043080")));
            Assert.Equal(60, PatchClassifier.Label(new PendingUpdate { Title = new string('x', 90) }).Length);
        }

        [Fact]
        public void Iso_IsUtcWithZ()
        {
            var local = new DateTime(2026, 9, 27, 15, 0, 0, DateTimeKind.Local);
            Assert.Equal(local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), PatchClassifier.Iso(local));
            Assert.EndsWith("Z", PatchClassifier.Iso(DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Unspecified)));
        }
    }

    public class PatchScheduleTests : TestBase
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 28, 7, 55, 0, DateTimeKind.Utc);

        [Fact]
        public void StartupDelay_IsPerAgentBetween10And70Minutes()
        {
            var delays = Enumerable.Range(0, 200).Select(i => PatchSchedule.StartupDelay($"HW-{i:X12}")).ToList();
            Assert.All(delays, d => Assert.InRange(d, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(70)));
            // Aynı ajan her açılışta aynı gecikmeyi alır; farklı ajanlar yayılır
            Assert.Equal(PatchSchedule.StartupDelay("HW-678CC8C5265E"), PatchSchedule.StartupDelay("HW-678CC8C5265E"));
            Assert.True(delays.Select(d => (int)d.TotalMinutes).Distinct().Count() > 40);
        }

        [Fact]
        public void NoPreviousScan_ScansAfterStartupDelay()
        {
            DateTime due = PatchSchedule.NextScanUtc(null, null, Start, Start, "HW-A");
            Assert.Equal(Start + PatchSchedule.StartupDelay("HW-A"), due);
        }

        [Fact]
        public void RecentScan_WaitsADay()
        {
            DateTime last = Start.AddHours(-2);
            Assert.Equal(last.AddDays(1), PatchSchedule.NextScanUtc(last, null, Start, Start, "HW-A"));
        }

        [Fact]
        public void OldScan_ScansAfterStartupDelay()
        {
            DateTime last = Start.AddDays(-3);
            Assert.Equal(Start + PatchSchedule.StartupDelay("HW-A"), PatchSchedule.NextScanUtc(last, null, Start, Start, "HW-A"));
        }

        [Fact]
        public void FailedAttempt_RetriesAfterAnHour()
        {
            DateTime attempt = Start.AddHours(2);
            Assert.Equal(attempt.AddHours(1), PatchSchedule.NextScanUtc(Start.AddDays(-2), attempt, Start, attempt, "HW-A"));
        }

        [Fact]
        public void ScanFromTheFuture_IsIgnored()
        {
            DateTime future = Start.AddDays(30);
            Assert.Equal(Start + PatchSchedule.StartupDelay("HW-A"), PatchSchedule.NextScanUtc(future, null, Start, Start, "HW-A"));
        }

        [Fact]
        public void State_SurvivesRestart()
        {
            string statePath = AgentHarness.Create("patch").Paths.DataFile(PatchManager.StateFileName);
            if (File.Exists(statePath)) File.Delete(statePath);
            Assert.Null(PatchManager.LoadState(statePath).LastScanUtc);
            DateTime t = new DateTime(2026, 9, 27, 9, 30, 0, DateTimeKind.Utc);
            var pending = PatchClassifier.BuildStatus(new List<PendingUpdate> { new PendingUpdate { Kb = "KB1", Title = "t" } }, true, t, null, "özet");
            PatchManager.SaveState(statePath, new PatchState { LastScanUtc = t, PendingReport = pending, NextPostUtc = t.AddMinutes(30) });

            PatchState loaded = PatchManager.LoadState(statePath);
            Assert.Equal(t, loaded.LastScanUtc);
            Assert.Equal(t.AddMinutes(30), loaded.NextPostUtc);
            Assert.Equal("KB1", loaded.PendingReport.Updates.Single().Kb);
            Assert.Equal("özet", loaded.PendingReport.LastResult);
            if (File.Exists(statePath)) File.Delete(statePath);
        }

        // M2: gönderim başarısızsa yalnızca GÖNDERİM yeniden denenir; tam tarama günde bir kalır
        [Fact]
        public void FailedPost_RetriesThePostNotTheScan()
        {
            DateTime scanned = Start.AddHours(1);
            DateTime? retry = PatchSchedule.NextPostUtc(PostResult.Failed, scanned);
            Assert.Equal(scanned + PatchSchedule.PostRetryDelay, retry);

            Assert.Equal(PatchStep.Wait, PatchSchedule.NextStep(scanned, scanned, retry, Start, scanned.AddMinutes(10), "HW-A"));
            Assert.Equal(PatchStep.RetryPost, PatchSchedule.NextStep(scanned, scanned, retry, Start, scanned.AddMinutes(31), "HW-A"));
            // Gönderim saatlerce başarısız olsa da tarama ancak 24 saat sonra
            Assert.Equal(PatchStep.Wait, PatchSchedule.NextStep(scanned, scanned, null, Start, scanned.AddHours(23), "HW-A"));
            Assert.Equal(PatchStep.Scan, PatchSchedule.NextStep(scanned, scanned, null, Start, scanned.AddHours(24), "HW-A"));
        }

        [Fact]
        public void MissingEndpointOrSuccess_DropsThePendingPost()
        {
            Assert.Null(PatchSchedule.NextPostUtc(PostResult.Sent, Start));
            Assert.Null(PatchSchedule.NextPostUtc(PostResult.EndpointMissing, Start));
            Assert.NotNull(PatchSchedule.NextPostUtc(PostResult.NotSent, Start));
        }
    }

    // M2: gönderilemeyen durum saklanır, yalnızca gönderim yeniden denenir; uç yoksa bırakılır
    [Collection(SharedStateCollection.Name)]
    public class PatchDeliveryTests : SharedStateTestBase
    {
        // Testin veri klasörü (patch-scan.json)
        private readonly AgentPaths _paths = AgentHarness.Create("patch").Paths;

        private string StatePath => _paths.DataFile(PatchManager.StateFileName);

        private static PatchStatusPayload Status(string lastResult = null) =>
            PatchClassifier.BuildStatus(new List<PendingUpdate> { new PendingUpdate { Kb = "KB1", Title = "t", Severity = "Critical" } }, false, DateTime.UtcNow, null, lastResult);

        private PatchManager Manager(PostResult result) =>
            new PatchManager(_paths, "https://pops.example", () => "HW-A") { Poster = _ => Task.FromResult(result) };

        [Fact]
        public async Task FailedPost_KeepsTheReportForARetry()
        {
            DateTime lastScan = DateTime.UtcNow.AddMinutes(-2);
            PatchManager.SaveState(StatePath, new PatchState { LastScanUtc = lastScan });

            await Manager(PostResult.Failed).DeliverAsync(Status("2 güncelleme kuruldu"));

            PatchState state = PatchManager.LoadState(StatePath);
            Assert.Equal("2 güncelleme kuruldu", state.PendingReport.LastResult);
            Assert.InRange(state.NextPostUtc.Value, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));
            // Tarama zamanı değişmez: sonraki tam tarama yine 24 saat sonra
            Assert.Equal(lastScan, state.LastScanUtc);
        }

        [Fact]
        public async Task SuccessfulRetry_ClearsTheReport()
        {
            await Manager(PostResult.Failed).DeliverAsync(Status());
            await Manager(PostResult.Sent).DeliverAsync(PatchManager.LoadState(StatePath).PendingReport);
            PatchState state = PatchManager.LoadState(StatePath);
            Assert.Null(state.PendingReport);
            Assert.Null(state.NextPostUtc);
        }

        [Fact]
        public async Task MissingEndpoint_DropsTheReport()
        {
            await Manager(PostResult.EndpointMissing).DeliverAsync(Status());
            Assert.Null(PatchManager.LoadState(StatePath).PendingReport);
        }

        [Fact]
        public void InvalidScope_StartsNothing()
        {
            PatchManager manager = Manager(PostResult.Sent);
            manager.RequestInstall("security\r\n[AGENT] sahte");
            Assert.False(manager.IsBusy);
        }
    }
}
