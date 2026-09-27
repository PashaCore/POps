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
        private readonly string _previousDataDir = AgentUpdate.DataDir;

        public RollbackDrillTests()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("drill");
            Directory.CreateDirectory(AgentUpdate.SecureDataDir);
        }

        public void Dispose()
        {
            AgentUpdate.DataDir = _previousDataDir;
            AgentUpdate.InstalledVersionOverride = null;
        }

        private static AgentUpdate.UpdateRun Run(string to, long startedAt) => new AgentUpdate.UpdateRun { ToVersion = to, StartedAt = startedAt };
        private static AgentUpdate.ConsumedDrill Consumed(string version, long startedAt) => new AgentUpdate.ConsumedDrill { Version = version, UpdateStartedAt = startedAt };

        private static void WriteLock(string from, string to, long startedAt) =>
            File.WriteAllText(AgentUpdate.LockPath, JsonSerializer.Serialize(new { from_version = from, to_version = to, started_at = startedAt }));

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
            File.WriteAllText(AgentUpdate.RollbackDrillPath, "");
            WriteLock("0.1.5-alpha", "0.1.6-alpha", 1000);

            // 1) Yeni sürüm açıldı: işaret tüketilir, sağlık bildirilmez
            AgentUpdate.InstalledVersionOverride = "0.1.6-alpha";
            Assert.True(AgentUpdate.ApplyRollbackDrillOnStartup());
            Assert.False(File.Exists(AgentUpdate.RollbackDrillPath));
            Assert.Contains("0.1.6-alpha", File.ReadAllText(AgentUpdate.ConsumedDrillPath));

            // 2) SCM yeni sürümü yeniden başlattı: tatbikat sürer
            Assert.True(AgentUpdate.ApplyRollbackDrillOnStartup());

            // 3) Geri kurulan 0.1.5 işareti tanımıyordu (yalnızca rollback-drill'e bakar): artık görmez, sağlık bildirir
            Assert.False(AgentUpdate.RollbackDrillRequested());
            //    Yeni kodlu eski sürüm de sağlık bildirir ve başkasının kaydını siler
            AgentUpdate.InstalledVersionOverride = "0.1.5-alpha";
            Assert.False(AgentUpdate.ApplyRollbackDrillOnStartup());
            Assert.False(File.Exists(AgentUpdate.ConsumedDrillPath));
        }

        [Fact]
        public void OldUpdaterLeftTheRecord_NextSendIsNormal()
        {
            // 0.1.5 updater yalnızca rollback-drill'i siler; .consumed kalır. 0.1.6 yeniden gönderilince yeni çalışma başlar.
            File.WriteAllText(AgentUpdate.RollbackDrillPath, "");
            WriteLock("0.1.5-alpha", "0.1.6-alpha", 1000);
            AgentUpdate.InstalledVersionOverride = "0.1.6-alpha";
            Assert.True(AgentUpdate.ApplyRollbackDrillOnStartup());

            WriteLock("0.1.5-alpha", "0.1.6-alpha", 2000);
            Assert.False(AgentUpdate.ApplyRollbackDrillOnStartup());
            Assert.False(File.Exists(AgentUpdate.ConsumedDrillPath));
        }

        [Fact]
        public void MarkerPlacedBeforeTheUpdate_SurvivesAServiceRestart()
        {
            File.WriteAllText(AgentUpdate.RollbackDrillPath, "");
            AgentUpdate.InstalledVersionOverride = "0.1.6-alpha";
            Assert.False(AgentUpdate.ApplyRollbackDrillOnStartup());   // güncelleme yok: servis yeniden başladı
            Assert.True(AgentUpdate.RollbackDrillRequested());
        }

        [Fact]
        public void UnreadableState_StillReportsHealth()
        {
            File.WriteAllText(AgentUpdate.RollbackDrillPath, "");
            File.WriteAllText(AgentUpdate.LockPath, "{bozuk");
            AgentUpdate.InstalledVersionOverride = "0.1.6-alpha";
            Assert.False(AgentUpdate.ApplyRollbackDrillOnStartup());
        }

        // ------------------------------------------------------------------ updater temizliği

        [Fact]
        public void UpdaterClear_RemovesBothMarkers()
        {
            File.WriteAllText(AgentUpdate.RollbackDrillPath, "");
            File.WriteAllText(AgentUpdate.ConsumedDrillPath, "{}");
            Assert.Equal(2, RollbackDrill.Clear(AgentUpdate.SecureDataDir, null));
            Assert.False(File.Exists(AgentUpdate.RollbackDrillPath));
            Assert.False(File.Exists(AgentUpdate.ConsumedDrillPath));
            Assert.Equal(0, RollbackDrill.Clear(AgentUpdate.SecureDataDir, null));
        }

        [Fact]
        public void SharedFileNames_MatchTheAgentPaths()
        {
            Assert.Equal(RollbackDrill.MarkerFileName, Path.GetFileName(AgentUpdate.RollbackDrillPath));
            Assert.Equal(RollbackDrill.ConsumedFileName, Path.GetFileName(AgentUpdate.ConsumedDrillPath));
        }
    }
}
