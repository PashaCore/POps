using System;
using System.IO;
using System.Text.Json;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Geri dönüş tatbikatı: işaret, güncellemeyle kurulan yeni sürümün ilk açılışında tüketilir. Sahada 0.1.4 -> 0.1.5
    // tatbikatı "rollback_failed" vermişti: geri kurulan 0.1.4 de işareti görüp sağlık bildirmemişti.
    public class RollbackDrillTests : TestBase, IDisposable
    {
        // Testin klasörleri (güvenli depo dahil) ve açılan sürüm
        private readonly AgentHarness _agent = AgentHarness.Create("drill");
        private string _installedVersion;

        public void Dispose() => _agent.Dispose();

        // Servisin açılışı: bu klasörlerde, _installedVersion sürümüyle
        private bool ApplyRollbackDrillOnStartup() => new AgentUpdater(_agent.Paths, _installedVersion).ApplyRollbackDrillOnStartup();

        private static AgentUpdate.UpdateRun Run(string to, long startedAt) => new AgentUpdate.UpdateRun { ToVersion = to, StartedAt = startedAt };
        private static AgentUpdate.ConsumedDrill Consumed(string version, long startedAt) => new AgentUpdate.ConsumedDrill { Version = version, UpdateStartedAt = startedAt };

        private void WriteLock(string from, string to, long startedAt) =>
            File.WriteAllText(_agent.Paths.UpdateLockPath, JsonSerializer.Serialize(new { from_version = from, to_version = to, started_at = startedAt }));

        // ------------------------------------------------------------------ karar (dosyasız)

        [Fact]
        public void MarkerDuringUpdateToMe_IsConsumedAndHealthSkipped()
        {
            var d = AgentUpdate.DecideDrill(true, null, Run("0.1.6-alpha", 1000), "0.1.6-alpha");
            Assert.True(d.Consume);
            Assert.True(d.SkipHealth);
        }

        [Fact]
        public void MarkerWithoutAnUpdate_IsKeptForTheNextUpdate()
        {
            var d = AgentUpdate.DecideDrill(true, null, null, "0.1.6-alpha");
            Assert.False(d.Consume);
            Assert.False(d.SkipHealth);
        }

        [Fact]
        public void RolledBackVersion_ReportsHealth()
        {
            // Geri kurulan sürüm (yeni kodla) güncellemenin hedefi değildir; .consumed başkasınındır
            var d = AgentUpdate.DecideDrill(false, Consumed("0.1.6-alpha", 1000), Run("0.1.6-alpha", 1000), "0.1.5-alpha");
            Assert.False(d.SkipHealth);
            Assert.True(d.DiscardConsumed);
        }

        [Fact]
        public void RestartInTheSameRun_KeepsTheDrill()
        {
            var d = AgentUpdate.DecideDrill(false, Consumed("0.1.6-alpha", 1000), Run("v0.1.6-alpha", 1000), "0.1.6-alpha");
            Assert.True(d.SkipHealth);
            Assert.False(d.DiscardConsumed);
        }

        [Fact]
        public void SameVersionSentAgain_InstallsNormally()
        {
            // Eski updater .consumed'ı bırakmış olabilir; yeni çalışma (başka started_at) onu geçersiz kılar
            var d = AgentUpdate.DecideDrill(false, Consumed("0.1.6-alpha", 1000), Run("0.1.6-alpha", 2000), "0.1.6-alpha");
            Assert.False(d.SkipHealth);
            Assert.True(d.DiscardConsumed);
        }

        [Fact]
        public void StaleConsumedRecord_WithANewMarker_StartsANewDrill()
        {
            var d = AgentUpdate.DecideDrill(true, Consumed("0.1.6-alpha", 1000), Run("0.1.7-alpha", 3000), "0.1.7-alpha");
            Assert.True(d.DiscardConsumed);
            Assert.True(d.Consume);
            Assert.True(d.SkipHealth);
        }

        [Theory]
        [InlineData("0.1.6-alpha", "v0.1.6-alpha", true)]
        [InlineData("0.1.6-alpha", "0.1.6-alpha+build.7", true)]
        [InlineData("0.1.6-alpha", "0.1.5-alpha", false)]
        [InlineData("0.1.6-alpha", null, false)]
        public void VersionsAreCompared(string a, string b, bool same) => Assert.Equal(same, AgentUpdate.SameVersion(a, b));

        // ------------------------------------------------------------------ dosyalarla bütün akış

        [Fact]
        public void DrillFlow_OnDisk()
        {
            File.WriteAllText(_agent.Paths.RollbackDrillPath, "");
            WriteLock("0.1.5-alpha", "0.1.6-alpha", 1000);

            // 1) Yeni sürüm açıldı: işaret tüketilir, sağlık bildirilmez
            _installedVersion = "0.1.6-alpha";
            Assert.True(ApplyRollbackDrillOnStartup());
            Assert.False(File.Exists(_agent.Paths.RollbackDrillPath));
            Assert.Contains("0.1.6-alpha", File.ReadAllText(_agent.Paths.ConsumedDrillPath));

            // 2) SCM yeni sürümü yeniden başlattı: tatbikat sürer
            Assert.True(ApplyRollbackDrillOnStartup());

            // 3) Geri kurulan 0.1.5 işareti tanımıyordu (yalnızca rollback-drill'e bakar): artık görmez, sağlık bildirir
            Assert.False(AgentUpdate.RollbackDrillRequested(_agent.Paths));
            //    Yeni kodlu eski sürüm de sağlık bildirir ve başkasının kaydını siler
            _installedVersion = "0.1.5-alpha";
            Assert.False(ApplyRollbackDrillOnStartup());
            Assert.False(File.Exists(_agent.Paths.ConsumedDrillPath));
        }

        [Fact]
        public void OldUpdaterLeftTheRecord_NextSendIsNormal()
        {
            // 0.1.5 updater yalnızca rollback-drill'i siler; .consumed kalır. 0.1.6 yeniden gönderilince yeni çalışma başlar.
            File.WriteAllText(_agent.Paths.RollbackDrillPath, "");
            WriteLock("0.1.5-alpha", "0.1.6-alpha", 1000);
            _installedVersion = "0.1.6-alpha";
            Assert.True(ApplyRollbackDrillOnStartup());

            WriteLock("0.1.5-alpha", "0.1.6-alpha", 2000);
            Assert.False(ApplyRollbackDrillOnStartup());
            Assert.False(File.Exists(_agent.Paths.ConsumedDrillPath));
        }

        [Fact]
        public void MarkerPlacedBeforeTheUpdate_SurvivesAServiceRestart()
        {
            File.WriteAllText(_agent.Paths.RollbackDrillPath, "");
            _installedVersion = "0.1.6-alpha";
            Assert.False(ApplyRollbackDrillOnStartup());   // güncelleme yok: servis yeniden başladı
            Assert.True(AgentUpdate.RollbackDrillRequested(_agent.Paths));
        }

        [Fact]
        public void UnreadableState_StillReportsHealth()
        {
            File.WriteAllText(_agent.Paths.RollbackDrillPath, "");
            File.WriteAllText(_agent.Paths.UpdateLockPath, "{bozuk");
            _installedVersion = "0.1.6-alpha";
            Assert.False(ApplyRollbackDrillOnStartup());
        }

        // ------------------------------------------------------------------ updater temizliği

        [Fact]
        public void UpdaterClear_RemovesBothMarkers()
        {
            File.WriteAllText(_agent.Paths.RollbackDrillPath, "");
            File.WriteAllText(_agent.Paths.ConsumedDrillPath, "{}");
            Assert.Equal(2, RollbackDrill.Clear(_agent.Paths.SecureDir, null));
            Assert.False(File.Exists(_agent.Paths.RollbackDrillPath));
            Assert.False(File.Exists(_agent.Paths.ConsumedDrillPath));
            Assert.Equal(0, RollbackDrill.Clear(_agent.Paths.SecureDir, null));
        }

        [Fact]
        public void SharedFileNames_MatchTheAgentPaths()
        {
            Assert.Equal(RollbackDrill.MarkerFileName, Path.GetFileName(_agent.Paths.RollbackDrillPath));
            Assert.Equal(RollbackDrill.ConsumedFileName, Path.GetFileName(_agent.Paths.ConsumedDrillPath));
        }
    }

    public class StaleLockDrillTests : TestBase, IDisposable
    {
        // Testin klasörleri (güvenli depo dahil) ve açılan sürüm
        private readonly AgentHarness _agent = AgentHarness.Create("stale");
        private string _installedVersion;

        public void Dispose() => _agent.Dispose();

        // Servisin açılışı: bu klasörlerde, _installedVersion sürümüyle
        private bool ApplyRollbackDrillOnStartup() => new AgentUpdater(_agent.Paths, _installedVersion).ApplyRollbackDrillOnStartup();

        // L4: çökmüş bir updater'dan kalan eski kilit "güncelleme var" sayılmaz; işaret tüketilmez
        [Fact]
        public void StaleUpdateLock_DoesNotConsumeTheMarker()
        {
            File.WriteAllText(_agent.Paths.RollbackDrillPath, "");
            File.WriteAllText(_agent.Paths.UpdateLockPath, JsonSerializer.Serialize(new { from_version = "0.1.5-alpha", to_version = "0.1.6-alpha", started_at = 1000 }));
            File.SetLastWriteTimeUtc(_agent.Paths.UpdateLockPath, DateTime.UtcNow.AddMinutes(-20));
            _installedVersion = "0.1.6-alpha";

            Assert.False(ApplyRollbackDrillOnStartup());
            Assert.True(AgentUpdate.RollbackDrillRequested(_agent.Paths));
        }
    }
}
