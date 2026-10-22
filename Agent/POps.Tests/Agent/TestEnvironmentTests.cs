using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Testler hiçbir zaman gerçek C:\POpsData, C:\POpsData\secure, C:\POpsLogs ya da C:\POps klasörlerine yazmaz: ajanın
    // yolları her testte AgentHarness'in geçici klasörlerindedir (TestEnvironment.Root altında). Bu sınıf hiçbir statiğe
    // dokunmaz, paralel çalışır.
    public class TestEnvironmentTests : TestBase
    {
        private static IEnumerable<string> EveryPath(AgentPaths paths) =>
            new[]
            {
                paths.DataDir, paths.SecureDir, paths.LogDir, paths.ServerCaPath, paths.IdentityPath, paths.HealthPath,
                paths.UpdateLockPath, paths.UpdateResultPath, paths.ReportedResultPath, paths.UpdateProgressPath, paths.UpdatesDir,
                paths.UpdaterDir, paths.RollbackDrillPath, paths.ConsumedDrillPath, paths.InboxDir, paths.PeerCacheDir,
                paths.DataFile("x.json"), paths.SecureFile("x.json"),
            }.Concat(paths.ConfigPaths);

        [Fact]
        public void HarnessPaths_NeverPointAtTheRealFolders()
        {
            using var agent = AgentHarness.Create();
            string root = TestEnvironment.Root + Path.DirectorySeparatorChar;
            foreach (string path in EveryPath(agent.Paths))
            {
                Assert.StartsWith(root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
                Assert.False(Path.GetFullPath(path).StartsWith(@"C:\POps", StringComparison.OrdinalIgnoreCase), path);
            }
            // Ayar dosyası yok: gerçek C:\POps\appsettings.json okunmaz
            Assert.Empty(agent.Paths.ConfigPaths);
            Assert.Same(agent.Paths, agent.Context.Paths);
            Assert.True(Directory.Exists(agent.Paths.SecureDir));
            Assert.True(Directory.Exists(agent.Paths.LogDir));
        }

        // Her test kendi klasörlerini alır; harness atılınca silinir
        [Fact]
        public void EachHarness_HasItsOwnFolders()
        {
            string first;
            using (var a = AgentHarness.Create())
            using (var b = AgentHarness.Create())
            {
                Assert.NotEqual(a.Paths.DataDir, b.Paths.DataDir, StringComparer.OrdinalIgnoreCase);
                Assert.False(EveryPath(a.Paths).Intersect(EveryPath(b.Paths), StringComparer.OrdinalIgnoreCase).Any());
                first = a.Paths.DataDir;
                File.WriteAllText(a.Paths.SecureFile("x.json"), "{}");
            }
            Assert.False(Directory.Exists(first));
        }

        // Servisin yerleşimi değişmedi: güvenli depo ve kurum sertifikası veri klasörünün içinde, updater ve watchdog aynı
        // klasörleri alır (yalnızca yollar hesaplanır, hiçbir klasöre dokunulmaz)
        [Fact]
        public void ServiceDefaults_AreTheInstalledFolders()
        {
            AgentPaths paths = AgentPaths.ForFolders(FolderSettings.DefaultDataDirectory, FolderSettings.DefaultLogDirectory, POpsHelpers.DefaultConfigPaths);
            Assert.Equal(@"C:\POpsData", paths.DataDir);
            Assert.Equal(@"C:\POpsData\secure", paths.SecureDir);
            Assert.Equal(SecureStore.DefaultDir, paths.SecureDir);
            Assert.Equal(@"C:\POpsLogs", paths.LogDir);
            Assert.Equal(@"C:\POpsData\secure\server-ca.pem", paths.ServerCaPath);
            Assert.Equal(@"C:\POpsData\identity.key", paths.IdentityPath);
            Assert.Equal(@"C:\POpsData\update.lock", paths.UpdateLockPath);
            Assert.Equal(@"C:\POpsData\health.json", paths.HealthPath);
            Assert.Equal(@"C:\POpsData\update-result.json", paths.UpdateResultPath);
            Assert.Equal(@"C:\POpsData\secure\rollback-drill", paths.RollbackDrillPath);
            Assert.Equal(new[] { Path.Combine(AppContext.BaseDirectory, "appsettings.json"), @"C:\POps\appsettings.json" }, paths.ConfigPaths);
            Assert.Equal(new[] { "--datadir", @"C:\POpsData", "--logdir", @"C:\POpsLogs" }, paths.UpdaterArguments());
            Assert.Equal("--datadir \"C:\\POpsData\"", paths.WatchdogArguments());
            Assert.Null(paths.FolderProblem);
        }

        // Testlerin logu da geçici kökte (süreç başına bir kez kurulur; POpsHelpers.Log)
        [Fact]
        public void Logs_StayUnderTheTestRoot()
        {
            string root = TestEnvironment.Root + Path.DirectorySeparatorChar;
            Assert.StartsWith(root, Path.GetFullPath(POpsHelpers.LogDirectory), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(root, Path.GetFullPath(POpsHelpers.MachineLogDir), StringComparison.OrdinalIgnoreCase);
        }
    }
}
