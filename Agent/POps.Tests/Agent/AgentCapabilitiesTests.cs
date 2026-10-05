using System.IO;
using System.Linq;
using System.Text.Json;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class AgentCapabilitiesTests : TestBase
    {
        private static string CapabilitiesPath
        {
            get
            {
                SecureStore.Dir = TestEnvironment.NewDir("caps");
                return SecureStore.PathOf(AgentCapabilities.FileName);
            }
        }

        private static JsonElement Request(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void NoPolicyFile_BothEnabled()
        {
            _ = CapabilitiesPath;
            AgentCapabilities.Load();
            Assert.True(AgentCapabilities.TerminalEnabled);
            Assert.True(AgentCapabilities.VisionEnabled);
        }

        [Fact]
        public void Server_CanDisable_AndItPersists()
        {
            string path = CapabilitiesPath;
            AgentCapabilities.Load();
            var (disabled, ignored) = AgentCapabilities.ApplyServerRequest(Request("{\"action\":\"set_capabilities\",\"terminal_enabled\":false}"));

            Assert.Equal(new[] { "terminal_enabled" }, disabled);
            Assert.Empty(ignored);
            Assert.False(AgentCapabilities.TerminalEnabled);
            Assert.True(AgentCapabilities.VisionEnabled);
            Assert.True(SecureStore.IsLockedDown(new FileInfo(path).GetAccessControl()));

            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.TerminalEnabled);
        }

        [Fact]
        public void Server_CannotEnable()
        {
            _ = CapabilitiesPath;
            AgentCapabilities.Load();
            AgentCapabilities.ApplyServerRequest(Request("{\"terminal_enabled\":false,\"vision_enabled\":false}"));
            var (disabled, ignored) = AgentCapabilities.ApplyServerRequest(Request("{\"terminal_enabled\":true,\"vision_enabled\":true}"));

            Assert.Empty(disabled);
            Assert.Equal(new[] { "terminal_enabled", "vision_enabled" }, ignored.OrderBy(n => n));
            Assert.False(AgentCapabilities.TerminalEnabled);
            Assert.False(AgentCapabilities.VisionEnabled);
        }

        [Fact]
        public void NonBooleanValues_AreIgnored()
        {
            _ = CapabilitiesPath;
            AgentCapabilities.Load();
            AgentCapabilities.ApplyServerRequest(Request("{\"terminal_enabled\":\"false\",\"vision_enabled\":0}"));
            Assert.True(AgentCapabilities.TerminalEnabled);
            Assert.True(AgentCapabilities.VisionEnabled);
        }

        [Fact]
        public void InstallerWrittenFile_IsRead()
        {
            string path = CapabilitiesPath;
            SecureStore.WriteProtected(path, "{\r\n  \"terminal_enabled\": true,\r\n  \"vision_enabled\": false,\r\n  \"source\": \"msi\",\r\n  \"updated_at\": 1790450000\r\n}\r\n");
            AgentCapabilities.Load();
            Assert.True(AgentCapabilities.TerminalEnabled);
            Assert.False(AgentCapabilities.VisionEnabled);
        }

        [Theory]
        [InlineData("{ not json")]
        [InlineData("{\"terminal_enabled\":\"yes\"}")]
        public void UnreadablePolicy_DisablesBoth(string content)
        {
            SecureStore.WriteProtected(CapabilitiesPath, content);
            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.TerminalEnabled);
            Assert.False(AgentCapabilities.VisionEnabled);
        }

        [Fact]
        public void StatusMessage_MatchesServerContract()
        {
            _ = CapabilitiesPath;
            AgentCapabilities.Load();
            AgentCapabilities.ApplyServerRequest(Request("{\"vision_enabled\":false}"));
            Assert.Equal("{\"type\":\"capabilities\",\"terminal_enabled\":true,\"vision_enabled\":false,\"server_ca\":\"system\",\"files_enabled\":true,"
                + "\"exam_enabled\":true,\"power_enabled\":true,\"message_enabled\":true,\"peer_cache_enabled\":true}", JsonSerializer.Serialize(AgentCapabilities.StatusMessage()));
        }
    }
}
