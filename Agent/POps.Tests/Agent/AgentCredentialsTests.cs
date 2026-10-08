using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    [Collection(SharedStateCollection.Name)]
    public class AgentCredentialsTests : SharedStateTestBase
    {
        private static string Urlsafe(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string NewStore()
        {
            SecureStore.Dir = TestEnvironment.NewDir("store");
            return SecureStore.Dir;
        }

        private static string Config(string json)
        {
            string path = Path.Combine(TestEnvironment.NewDir("cfg"), "appsettings.json");
            File.WriteAllText(path, json);
            POpsHelpers.ConfigPaths = new[] { path };
            return path;
        }

        [Fact]
        public void ServerIssuedValues_AreWellFormed()
        {
            Assert.True(AgentCredentials.IsWellFormed(Urlsafe(32))); // secret: token_urlsafe(32)
            Assert.True(AgentCredentials.IsWellFormed(Urlsafe(24))); // enroll token: token_urlsafe(24)
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("short")]
        [InlineData("abc+def/ghi=jklmnopqrs")]
        [InlineData("AAAAAAAAAAAAAAAAAAAA\r\nX-Evil: 1")]
        public void HeaderUnsafeValues_AreRejected(string value) => Assert.False(AgentCredentials.IsWellFormed(value));

        [Fact]
        public void Newest_PrefersNewerRecord_AndPrimaryOnTie()
        {
            var primary = AgentCredentials.ParseRecord("{\"secret\":\"" + Urlsafe(32) + "\",\"saved_at\":100}");
            var mirror = AgentCredentials.ParseRecord("{\"secret\":\"" + Urlsafe(32) + "\",\"saved_at\":200}");
            var tie = AgentCredentials.ParseRecord("{\"secret\":\"" + Urlsafe(32) + "\",\"saved_at\":200}");
            Assert.Same(mirror, AgentCredentials.Newest(primary, mirror));
            Assert.Same(tie, AgentCredentials.Newest(tie, mirror));
            Assert.Null(AgentCredentials.ParseRecord("{\"secret\":\"bad secret\"}"));
        }

        [Fact]
        public void FreezeRollback_NewerMirrorSecretIsRestored()
        {
            NewStore();
            string mirror = TestEnvironment.NewDir("thaw");
            Config("{\"PersistDir\":\"" + mirror.Replace("\\", "\\\\") + "\"}");
            string secret = Urlsafe(32);
            Assert.True(AgentCredentials.SaveSecret(secret, "HW-TEST"));

            // C: geri alındı: birincil depoda eski secret
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCredentials.SecretFileName), "{\"secret\":\"" + Urlsafe(32) + "\",\"saved_at\":1}");
            Assert.Equal(secret, AgentCredentials.LoadSecret());
            Assert.Equal(secret, AgentCredentials.ParseRecord(SecureStore.Read(SecureStore.PathOf(AgentCredentials.SecretFileName))).Secret);
            Assert.Equal(secret, AgentCredentials.CurrentSecret);
        }

        [Fact]
        public void SecretsInAppSettings_MoveToTheStore()
        {
            NewStore();
            string token = Urlsafe(24);
            string config = Config("{\"ServerUrl\":\"https://pops.example\",\"BypassSecret\":\"bp-ÇĞ\",\"EnrollToken\":\"" + token + "\",\"Logging\":{\"LogLevel\":{\"Default\":\"Information\"}}}");

            AgentCredentials.MigrateSecrets();

            string rewritten = File.ReadAllText(config);
            Assert.DoesNotContain("BypassSecret", rewritten);
            Assert.DoesNotContain("EnrollToken", rewritten);
            Assert.Contains("https://pops.example", rewritten);
            Assert.Contains("Information", rewritten);
            Assert.Equal("bp-ÇĞ", AgentCredentials.GetBypassSecret());
            Assert.Equal(token, AgentCredentials.GetEnrollToken());
            Assert.True(SecureStore.IsLockedDown(new FileInfo(config).GetAccessControl()));
        }

        [Fact]
        public void DeviceBypassSecret_IsStoredProtected_AndPresenceIsSeparateFromReadValue()
        {
            NewStore();
            const string secret = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE";
            Assert.True(AgentCredentials.SaveDeviceBypassSecret(secret));
            Assert.Equal(secret, AgentCredentials.GetDeviceBypassSecret(out bool present));
            Assert.True(present);
            Assert.True(SecureStore.IsLockedDown(new FileInfo(SecureStore.PathOf(AgentCredentials.DeviceBypassSecretFileName)).GetAccessControl()));
        }

        [Theory]
        [InlineData("https://pops.example/api/inventory/HW-TEST", true)]
        [InlineData("http://127.0.0.1:8000/api/policy_alert", true)]
        [InlineData("http://10.0.0.5:8000/api/inventory/HW-TEST", false)]
        public void HttpAuthHeaders_OnlyOverSecureTransport(string url, bool expected)
        {
            NewStore();
            Config("{}");
            string secret = Urlsafe(32);
            AgentCredentials.SaveSecret(secret, "HW-TEST");

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            AgentCredentials.AddHttpAuth(request, "HW-TEST");
            Assert.Equal(expected, request.Headers.Contains("X-Agent-Secret"));
            if (expected)
            {
                Assert.Equal(secret, request.Headers.GetValues("X-Agent-Secret").Single());
                Assert.Equal("HW-TEST", request.Headers.GetValues("X-Agent-Id").Single());
            }
        }
    }
}
