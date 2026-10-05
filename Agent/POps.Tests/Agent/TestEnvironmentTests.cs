using System;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Testler hiçbir zaman gerçek C:\POpsData / C:\POpsData\secure klasörlerine yazmaz: bir test gerçek yolu geri
    // bırakırsa sonraki testin temel kurucusu onu geçici klasöre çevirir
    public class TestEnvironmentTests : TestBase
    {
        private sealed class NextTest : TestBase { }

        [Fact]
        public void RealFoldersAreNeverUsed()
        {
            AgentUpdate.DataDir = @"C:\POpsData";
            SecureStore.Dir = @"C:\POpsData\secure";
            _ = new NextTest();
            Assert.Equal(TestEnvironment.DefaultDataDir, AgentUpdate.DataDir);
            Assert.Equal(TestEnvironment.DefaultSecureDir, SecureStore.Dir);
            Assert.StartsWith(System.IO.Path.GetTempPath(), AgentUpdate.DataDir, StringComparison.OrdinalIgnoreCase);
        }
    }
}
