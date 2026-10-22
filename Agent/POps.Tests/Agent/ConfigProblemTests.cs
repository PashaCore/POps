using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Yapılandırma okunamazsa ajan sessizce 127.0.0.1'e düşmez: sorun loglanır, Olay Günlüğüne yazılır, tepside görünür
    [Collection(SharedStateCollection.Name)]
    public class ConfigProblemTests : SharedStateTestBase, IDisposable
    {
        public void Dispose() => POpsHelpers.ConfigPaths = new string[0];

        private static string Config(string content)
        {
            string path = Path.Combine(TestEnvironment.NewDir("config"), "appsettings.json");
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void ValidConfig_HasNoProblem()
        {
            POpsHelpers.ConfigPaths = new[] { Config("{\"ServerUrl\":\"https://pops.example/\"}") };
            Assert.Equal(("https://pops.example", (string)null), POpsHelpers.ResolveServerUrl());
        }

        [Fact]
        public void NoConfigFile_FallsBack_WithAProblem()
        {
            POpsHelpers.ConfigPaths = new[] { Path.Combine(TestEnvironment.Root, "yok", "appsettings.json") };
            var (url, problem) = POpsHelpers.ResolveServerUrl();
            Assert.Equal(POpsHelpers.FallbackServerUrl, url);
            Assert.Contains("bulunamadı", problem);
        }

        [Theory]
        [InlineData("{bozuk", "okunamadı")]
        [InlineData("{\"Logging\":{}}", "tanımlı değil")]
        [InlineData("{\"ServerUrl\":\"   \"}", "tanımlı değil")]
        [InlineData("{\"ServerUrl\":\"pops.example\"}", "geçerli bir http(s) adresi değil")]
        [InlineData("{\"ServerUrl\":\"ftp://pops.example\"}", "geçerli bir http(s) adresi değil")]
        [InlineData("[1,2]", "tanımlı değil")]
        public void UnreadableOrMissingServerUrl_FallsBack_WithAProblem(string content, string expected)
        {
            POpsHelpers.ConfigPaths = new[] { Config(content) };
            var (url, problem) = POpsHelpers.ResolveServerUrl();
            Assert.Equal(POpsHelpers.FallbackServerUrl, url);
            Assert.Contains(expected, problem);
        }

        // Kurulum klasöründeki dosya bozuk, eski konumdaki sağlam: adres bulunur ama sorun yine bildirilir
        [Fact]
        public void BrokenFirstFile_IsReported_EvenWhenAnotherHasTheUrl()
        {
            string broken = Config("{bozuk");
            POpsHelpers.ConfigPaths = new[] { broken, Config("{\"ServerUrl\":\"https://pops.example\"}") };
            var (url, problem) = POpsHelpers.ResolveServerUrl();
            Assert.Equal("https://pops.example", url);
            Assert.Contains(broken, problem);
        }

        [Fact]
        public void EnvironmentVariable_Wins()
        {
            POpsHelpers.ConfigPaths = new[] { Config("{\"ServerUrl\":\"https://dosya.example\"}") };
            Environment.SetEnvironmentVariable("POPS_SERVER_URL", "https://ortam.example");
            try { Assert.Equal(("https://ortam.example", (string)null), POpsHelpers.ResolveServerUrl()); }
            finally { Environment.SetEnvironmentVariable("POPS_SERVER_URL", null); }
        }

        [Fact]
        public void Worker_ShowsTheProblemInTheTray_AndAuditsItAsAnError()
        {
            POpsHelpers.ConfigPaths = new[] { Config("{bozuk") };
            using var worker = new Worker(NullLogger<Worker>.Instance, AgentHarness.FromStatics().Context);
            Assert.NotNull(worker.ConfigProblem);
            string message = worker.ConfigErrorMessage();
            Assert.StartsWith("CONFIG_ERROR:", message);
            Assert.Contains("okunamadı", Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring("CONFIG_ERROR:".Length))));
            worker.ReportConfigProblem();   // Olay Günlüğü yazılamasa da durmaz

            LocalAuditEvent audit = LocalAudit.ConfigUnreadable(worker.ConfigProblem, POpsHelpers.FallbackServerUrl);
            Assert.Equal(1090, audit.EventId);
            Assert.Equal(LocalAuditLevel.Error, audit.Level);
            Assert.Contains("server_url: http://127.0.0.1:8000", audit.Message);

            POpsHelpers.ConfigPaths = new[] { Config("{\"ServerUrl\":\"https://pops.example\"}") };
            using var healthy = new Worker(NullLogger<Worker>.Instance, AgentHarness.FromStatics().Context);
            Assert.Null(healthy.ConfigProblem);
            Assert.Null(healthy.ConfigErrorMessage());
        }
    }
}
