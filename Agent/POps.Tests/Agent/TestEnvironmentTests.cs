using System;
using System.IO;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Testler hiçbir zaman gerçek C:\POpsData, C:\POpsData\secure, C:\POpsLogs ya da C:\POps klasörlerine yazmaz: bir test
    // geçici kökün (TestEnvironment.Root) dışına dönen bir yol bırakırsa sonraki testin temel kurucusu onu geçici klasöre
    // çevirir. Bu, gerçek klasörle değil kökün dışındaki sahte bir yolla sınanır; hiçbir ayar bir an bile gerçek klasörü
    // göstermez.
    public class TestEnvironmentTests : TestBase
    {
        private sealed class NextTest : TestBase { }

        [Fact]
        public void APathOutsideTheTestRoot_IsPutBackBeforeTheNextTest()
        {
            // Testlerin geçici klasöründe (pops-tests), bu sürecin kökünün yanında; oluşturulmaz, içine yazılmaz
            string outside = Path.Combine(Path.GetDirectoryName(TestEnvironment.Root), "outside-" + Guid.NewGuid().ToString("N"));
            AgentUpdate.DataDir = outside;
            SecureStore.Dir = Path.Combine(outside, "secure");
            ServerTrust.CaPath = Path.Combine(outside, "secure", ServerTrust.FileName);
            POpsHelpers.MachineLogDir = Path.Combine(outside, "logs");

            _ = new NextTest();
            Assert.Equal(TestEnvironment.DefaultDataDir, AgentUpdate.DataDir);
            Assert.Equal(TestEnvironment.DefaultSecureDir, SecureStore.Dir);
            Assert.Equal(Path.Combine(TestEnvironment.DefaultSecureDir, ServerTrust.FileName), ServerTrust.CaPath);
            Assert.Equal(TestEnvironment.DefaultMachineLogDir, POpsHelpers.MachineLogDir);
            Assert.False(Directory.Exists(outside));
        }

        // Her test, yol ayarlarının hepsi geçici kökün içindeyken başlar: veri, güvenli depo, sunucu sertifikası, log ve
        // yapılandırma dosyaları (gerçek C:\POps\appsettings.json okunmaz)
        [Fact]
        public void EveryPathSeam_StartsUnderTheTestRoot()
        {
            string root = TestEnvironment.Root + Path.DirectorySeparatorChar;
            foreach (string path in new[] { AgentUpdate.DataDir, SecureStore.Dir, ServerTrust.CaPath, POpsHelpers.LogDirectory, POpsHelpers.MachineLogDir })
                Assert.StartsWith(root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            foreach (string path in POpsHelpers.ConfigPaths)
                Assert.StartsWith(root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
    }
}
