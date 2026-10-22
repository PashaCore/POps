using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class AgentCapabilitiesTests : TestBase, IDisposable
    {
        private AgentHarness _agent;

        public void Dispose() => _agent?.Dispose();

        // Her okunuşta yeni bir ajan (kendi güvenli deposu ve yetenek durumu); AgentCapabilities aşağıda onun durumudur
        private string CapabilitiesPath
        {
            get
            {
                _agent?.Dispose();
                _agent = AgentHarness.Create("caps");
                return _agent.Paths.SecureFile(AgentCapabilities.FileName);
            }
        }

        private CapabilityState Capabilities => _agent.Capabilities;

        private static JsonElement Request(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void NoPolicyFile_BothEnabled()
        {
            _ = CapabilitiesPath;
            Capabilities.Load();
            Assert.True(Capabilities.TerminalEnabled);
            Assert.True(Capabilities.VisionEnabled);
        }

        [Fact]
        public void Server_CanDisable_AndItPersists()
        {
            string path = CapabilitiesPath;
            Capabilities.Load();
            var (disabled, ignored) = Capabilities.ApplyServerRequest(Request("{\"action\":\"set_capabilities\",\"terminal_enabled\":false}"));

            Assert.Equal(new[] { "terminal_enabled" }, disabled);
            Assert.Empty(ignored);
            Assert.False(Capabilities.TerminalEnabled);
            Assert.True(Capabilities.VisionEnabled);
            Assert.True(SecureStore.IsLockedDown(new FileInfo(path).GetAccessControl()));

            Capabilities.Load();
            Assert.False(Capabilities.TerminalEnabled);
        }

        [Fact]
        public void Server_CannotEnable()
        {
            _ = CapabilitiesPath;
            Capabilities.Load();
            Capabilities.ApplyServerRequest(Request("{\"terminal_enabled\":false,\"vision_enabled\":false}"));
            var (disabled, ignored) = Capabilities.ApplyServerRequest(Request("{\"terminal_enabled\":true,\"vision_enabled\":true}"));

            Assert.Empty(disabled);
            Assert.Equal(new[] { "terminal_enabled", "vision_enabled" }, ignored.OrderBy(n => n));
            Assert.False(Capabilities.TerminalEnabled);
            Assert.False(Capabilities.VisionEnabled);
        }

        [Fact]
        public void NonBooleanValues_AreIgnored()
        {
            _ = CapabilitiesPath;
            Capabilities.Load();
            Capabilities.ApplyServerRequest(Request("{\"terminal_enabled\":\"false\",\"vision_enabled\":0}"));
            Assert.True(Capabilities.TerminalEnabled);
            Assert.True(Capabilities.VisionEnabled);
        }

        [Fact]
        public void InstallerWrittenFile_IsRead()
        {
            string path = CapabilitiesPath;
            SecureStore.WriteProtected(path, "{\r\n  \"terminal_enabled\": true,\r\n  \"vision_enabled\": false,\r\n  \"source\": \"msi\",\r\n  \"updated_at\": 1790450000\r\n}\r\n");
            Capabilities.Load();
            Assert.True(Capabilities.TerminalEnabled);
            Assert.False(Capabilities.VisionEnabled);
        }

        [Theory]
        [InlineData("{ not json")]
        [InlineData("{\"terminal_enabled\":\"yes\"}")]
        public void UnreadablePolicy_DisablesBoth(string content)
        {
            SecureStore.WriteProtected(CapabilitiesPath, content);
            Capabilities.Load();
            Assert.False(Capabilities.TerminalEnabled);
            Assert.False(Capabilities.VisionEnabled);
        }

        [Fact]
        public void StatusMessage_MatchesServerContract()
        {
            _ = CapabilitiesPath;
            Capabilities.Load();
            Capabilities.ApplyServerRequest(Request("{\"vision_enabled\":false}"));
            Assert.Equal("{\"type\":\"capabilities\",\"terminal_enabled\":true,\"vision_enabled\":false,\"server_ca\":\"system\",\"files_enabled\":true,"
                + "\"exam_enabled\":true,\"power_enabled\":true,\"message_enabled\":true,\"peer_cache_enabled\":true}", JsonSerializer.Serialize(Capabilities.StatusMessage()));
        }
    }
}
