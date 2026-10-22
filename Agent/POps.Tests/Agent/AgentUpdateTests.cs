using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    [Collection(SharedStateCollection.Name)]
    public class AgentUpdateTests : SharedStateTestBase
    {
        private sealed class FakeServer : HttpMessageHandler
        {
            public readonly List<string> Requests = new List<string>();
            public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri.AbsolutePath);
                return Task.FromResult(Respond(request));
            }
        }

        private static string NewDataDir()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("update");
            return AgentUpdate.DataDir;
        }

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        [Fact]
        public async Task UnsignedLegacyCommand_IsRejectedWithoutDownloading()
        {
            string dir = NewDataDir();
            var server = new FakeServer();
            await AgentUpdate.HandleUpdateCommandAsync(Json("{\"action\":\"update_agent\",\"download_url\":\"http://evil/x.zip\",\"hash\":\"" + new string('a', 64) + "\"}"), new HttpClient(server), "https://pops.example");
            Assert.Empty(server.Requests);
            Assert.False(Directory.Exists(Path.Combine(dir, "updates")));
        }

        [Fact]
        public async Task ManifestNotSignedByTheReleaseKey_IsRejectedWithoutDownloading()
        {
            NewDataDir();
            var server = new FakeServer();
            string command = JsonSerializer.Serialize(new
            {
                action = "update_agent",
                manifest = Convert.ToBase64String(File.ReadAllBytes(TestEnvironment.TestData("manifest.json"))),
                manifest_sig = File.ReadAllText(TestEnvironment.TestData("manifest.json.sig")),
            });
            await AgentUpdate.HandleUpdateCommandAsync(Json(command), new HttpClient(server), "https://pops.example");
            Assert.Empty(server.Requests);
        }

        private static (byte[] Bytes, ReleaseVerifier.Artifact Artifact) Package(int size)
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(size);
            return (bytes, new ReleaseVerifier.Artifact { Name = "POps-Agent-9.9.9-win-x64.msi", Size = size, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }

        private static FakeServer Serving(byte[] body) => new FakeServer { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) } };

        [Fact]
        public async Task Download_MatchingSizeAndHash_IsKept()
        {
            string dir = NewDataDir();
            var (bytes, artifact) = Package(300_000);
            string path = Path.Combine(dir, artifact.Name);
            var server = Serving(bytes);
            Assert.True(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(server), "https://pops.example/updates/" + artifact.Name, path, artifact));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".partial"));
            Assert.Equal("/updates/" + artifact.Name, server.Requests.Single());
        }

        [Fact]
        public async Task Download_SameSizeButDifferentBytes_IsDeleted()
        {
            string dir = NewDataDir();
            var (bytes, artifact) = Package(50_000);
            byte[] evil = (byte[])bytes.Clone();
            evil[1000] ^= 0xFF;
            string path = Path.Combine(dir, artifact.Name);
            Assert.False(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(Serving(evil)), "https://pops.example/updates/x", path, artifact));
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public async Task Download_LargerThanSigned_IsCutAndDeleted()
        {
            string dir = NewDataDir();
            var (bytes, artifact) = Package(50_000);
            string path = Path.Combine(dir, artifact.Name);
            Assert.False(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(Serving(bytes.Concat(new byte[4096]).ToArray())), "https://pops.example/updates/x", path, artifact));
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public async Task Download_HttpError_IsRejected()
        {
            string dir = NewDataDir();
            var (_, artifact) = Package(10);
            Assert.False(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(new FakeServer()), "https://pops.example/updates/x", Path.Combine(dir, artifact.Name), artifact));
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public void WriteOperationalHealth_WritesPhaseChecksVersionAndTimestamp()
        {
            NewDataDir();
            AgentUpdate.InstalledVersionOverride = "0.1.3-alpha";
            try
            {
                AgentUpdate.WriteOperationalHealth(new OperationalChecks
                {
                    Identity = true,
                    Credentials = true,
                    Capabilities = true,
                    Pipe = true,
                    Loop = true,
                });
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AgentUpdate.HealthPath));
                Assert.Equal("0.1.3-alpha", doc.RootElement.GetProperty("version").GetString());
                Assert.Equal("operational", doc.RootElement.GetProperty("phase").GetString());
                Assert.All(doc.RootElement.GetProperty("checks").EnumerateObject(), check => Assert.True(check.Value.GetBoolean()));
                Assert.InRange(doc.RootElement.GetProperty("ts").GetInt64(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);
                Assert.False(File.Exists(AgentUpdate.HealthPath + ".tmp"));
            }
            finally { AgentUpdate.InstalledVersionOverride = null; }
        }

        [Fact]
        public void WriteOperationalHealth_DoesNotWriteIncompleteChecks()
        {
            NewDataDir();
            AgentUpdate.WriteOperationalHealth(new OperationalChecks { Identity = true });
            Assert.False(File.Exists(AgentUpdate.HealthPath));
        }

        [Fact]
        public void PendingResult_IsReportedOnceWithStatusAndRunningVersion()
        {
            NewDataDir();
            Assert.Null(AgentUpdate.PendingResultMessage());
            File.WriteAllText(AgentUpdate.ResultPath, "{\"outcome\":\"rolled_back\",\"rollback\":\"msi_transaction\",\"running_version\":\"0.1.2-alpha\",\"from_version\":\"0.1.2-alpha\",\"to_version\":\"0.1.3-alpha\",\"msi_exit_code\":1603,\"reboot_required\":false,\"agent_state\":\"running\"}");

            Dictionary<string, object> message = AgentUpdate.PendingResultMessage();
            Assert.Equal("update_result", message["type"]);
            Assert.Equal("rolled_back", message["status"]);
            Assert.Equal("msi_transaction", message["rollback"]);
            Assert.Equal("0.1.2-alpha", message["running_version"]);
            Assert.Equal(1603, message["msi_exit_code"]);
            Assert.Equal(false, message["reboot_required"]);

            AgentUpdate.MarkResultReported();
            Assert.Null(AgentUpdate.PendingResultMessage());
            Assert.True(File.Exists(AgentUpdate.ReportedResultPath));
        }

        [Fact]
        public void UnreadableResult_IsNotSent()
        {
            NewDataDir();
            File.WriteAllText(AgentUpdate.ResultPath, "{broken");
            Assert.Null(AgentUpdate.PendingResultMessage());
        }

        [Fact]
        public void RollbackDrillMarker_IsDetected()
        {
            string dir = NewDataDir();
            Assert.False(AgentUpdate.RollbackDrillRequested());
            Directory.CreateDirectory(Path.Combine(dir, "secure"));
            File.WriteAllText(AgentUpdate.RollbackDrillPath, "");
            Assert.True(AgentUpdate.RollbackDrillRequested());
        }

        [Fact]
        public void UpdateLock_IsStaleAfterFifteenMinutes()
        {
            NewDataDir();
            File.WriteAllText(AgentUpdate.LockPath, "{}");
            Assert.True(AgentUpdate.IsLockFresh());
            File.SetLastWriteTimeUtc(AgentUpdate.LockPath, DateTime.UtcNow.AddMinutes(-16));
            Assert.False(AgentUpdate.IsLockFresh());
        }

        // Updater kurulum klasörü dışına deps.json'daki bütün bağımlılıklarıyla kopyalanır; POps.Shared.dll dahil
        private const string DepsJson = "{\"targets\":{\".NETCoreApp,Version=v8.0/win-x64\":{" +
            "\"POpsUpdater/0.1.3-alpha\":{\"dependencies\":{\"POps.Shared\":\"0.1.3-alpha\"},\"runtime\":{\"POpsUpdater.dll\":{}}}," +
            "\"POps.Shared/0.1.3-alpha\":{\"runtime\":{\"POps.Shared.dll\":{}}}," +
            "\"System.ServiceProcess.ServiceController/8.0.0\":{\"runtimeTargets\":{\"runtimes/win/lib/net8.0/System.ServiceProcess.ServiceController.dll\":{\"rid\":\"win\",\"assetType\":\"runtime\"}}}}}}";

        private static readonly string[] UpdaterClosure =
        {
            "POpsUpdater.exe", "POpsUpdater.dll", "POpsUpdater.deps.json", "POpsUpdater.runtimeconfig.json",
            "POps.Shared.dll", "System.ServiceProcess.ServiceController.dll",
        };

        [Fact]
        public void UpdaterFiles_IncludeTheSharedLibraryFromDepsJson()
        {
            string install = TestEnvironment.NewDir("install");
            foreach (string name in UpdaterClosure) File.WriteAllText(Path.Combine(install, name), name == "POpsUpdater.deps.json" ? DepsJson : "x");
            File.WriteAllText(Path.Combine(install, "POpsAgent.dll"), "not part of the updater");

            var names = AgentUpdate.UpdaterFiles(install).Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(UpdaterClosure.OrderBy(n => n), names);
        }

        [Fact]
        public void UpdaterFiles_MissingDependency_StopsTheUpdate()
        {
            string install = TestEnvironment.NewDir("install");
            foreach (string name in UpdaterClosure.Where(n => n != "POps.Shared.dll")) File.WriteAllText(Path.Combine(install, name), name == "POpsUpdater.deps.json" ? DepsJson : "x");
            Assert.Throws<FileNotFoundException>(() => AgentUpdate.UpdaterFiles(install).ToList());
        }
    }

    // A5: update_result sunucu onaylayana kadar saklanır
    public class UpdateResultAckTests : TestBase
    {
        private DateTime _now = new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);

        private UpdateResultReporter NewReporter()
        {
            var r = new UpdateResultReporter { UtcNow = () => _now };
            r.OnConnected();
            return r;
        }

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        private static readonly JsonElement AckServer = Json("{\"action\":\"server_info\",\"version\":\"0.1.14\",\"features\":[\"update_result_ack\"]}");

        [Fact]
        public void OldServer_WaitsFor15Seconds_ThenOldBehaviour()
        {
            UpdateResultReporter r = NewReporter();
            Assert.Equal(UpdateResultReporter.Step.Wait, r.Next("abc"));
            _now += TimeSpan.FromSeconds(14);
            Assert.Equal(UpdateResultReporter.Step.Wait, r.Next("abc"));
            _now += TimeSpan.FromSeconds(1);
            Assert.Equal(UpdateResultReporter.Step.SendAndMarkReported, r.Next("abc"));
        }

        [Fact]
        public void ServerInfoWithoutTheFeature_IsAnOldServer()
        {
            UpdateResultReporter r = NewReporter();
            r.OnServerInfo(Json("{\"action\":\"server_info\",\"version\":\"0.1.13\",\"features\":[\"other\"]}"));
            Assert.False(r.AckSupported);
            Assert.Equal(UpdateResultReporter.Step.SendAndMarkReported, r.Next("abc"));
        }

        [Fact]
        public void AckServer_KeepsTheResult_ResendsEvery60Seconds()
        {
            UpdateResultReporter r = NewReporter();
            r.OnServerInfo(AckServer);
            Assert.True(r.AckSupported);
            Assert.Equal(UpdateResultReporter.Step.SendAndKeep, r.Next("abc"));
            r.Sent("abc");
            _now += TimeSpan.FromSeconds(59);
            Assert.Equal(UpdateResultReporter.Step.Nothing, r.Next("abc"));
            _now += TimeSpan.FromSeconds(1);
            Assert.Equal(UpdateResultReporter.Step.SendAndKeep, r.Next("abc"));
            // Yeni bir sonuç beklemeden gönderilir
            Assert.Equal(UpdateResultReporter.Step.SendAndKeep, r.Next("def"));
        }

        [Fact]
        public void Reconnect_ResetsTheFlag()
        {
            UpdateResultReporter r = NewReporter();
            r.OnServerInfo(AckServer);
            r.OnConnected();
            Assert.False(r.AckSupported);
            Assert.Equal(UpdateResultReporter.Step.Wait, r.Next("abc"));
        }

        [Fact]
        public void NoResult_NothingToDo() => Assert.Equal(UpdateResultReporter.Step.Nothing, NewReporter().Next(null));

        [Theory]
        [InlineData("{\"action\":\"update_result_ack\",\"result_id\":\"abc\"}", "abc", true)]
        [InlineData("{\"action\":\"update_result_ack\",\"result_id\":\"xyz\"}", "abc", false)]
        [InlineData("{\"action\":\"update_result_ack\"}", "abc", false)]
        [InlineData("{\"action\":\"update_result_ack\",\"result_id\":1}", "abc", false)]
        [InlineData("{\"action\":\"update_result_ack\",\"result_id\":\"abc\"}", null, false)]
        [InlineData("[]", "abc", false)]
        public void Ack_MatchesOnlyThePendingResult(string ack, string pending, bool match) =>
            Assert.Equal(match, UpdateResultReporter.Acknowledges(Json(ack), pending));

        [Fact]
        public void ResultId_IsSha256OfTheRawFile_First32LowercaseHex()
        {
            AgentPaths paths = AgentHarness.Create("result-id").Paths;
            Assert.Null(AgentUpdate.PendingResultId(paths));
            byte[] raw = Encoding.UTF8.GetBytes("{\"outcome\":\"success\",\"to_version\":\"0.1.14-alpha\"}");
            File.WriteAllBytes(paths.UpdateResultPath, raw);

            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant().Substring(0, 32);
            Assert.Equal(expected, AgentUpdate.PendingResultId(paths));
            Assert.Equal(expected, AgentUpdate.PendingResultMessage(paths)["result_id"]);
            Assert.Equal(32, expected.Length);
            // Aynı sonuç için hep aynı değer
            Assert.Equal(AgentUpdate.PendingResultId(paths), AgentUpdate.PendingResultId(paths));
        }

        [Fact]
        public void ResultFile_StaysUntilAMatchingAck()
        {
            AgentPaths paths = AgentHarness.Create("result-ack").Paths;
            File.WriteAllText(paths.UpdateResultPath, "{\"outcome\":\"success\"}");
            UpdateResultReporter r = NewReporter();
            r.OnServerInfo(AckServer);
            string id = (string)AgentUpdate.PendingResultMessage(paths)["result_id"];
            Assert.Equal(UpdateResultReporter.Step.SendAndKeep, r.Next(id));
            r.Sent(id);

            // Yanlış kimlik: dosya kalır
            Assert.False(UpdateResultReporter.Acknowledges(Json("{\"action\":\"update_result_ack\",\"result_id\":\"0000\"}"), AgentUpdate.PendingResultId(paths)));
            Assert.True(File.Exists(paths.UpdateResultPath));

            // Doğru kimlik: kenara alınır
            Assert.True(UpdateResultReporter.Acknowledges(Json("{\"action\":\"update_result_ack\",\"result_id\":\"" + id + "\"}"), AgentUpdate.PendingResultId(paths)));
            AgentUpdate.MarkResultReported(paths);
            Assert.False(File.Exists(paths.UpdateResultPath));
            Assert.Null(AgentUpdate.PendingResultId(paths));
        }

        [Fact]
        public void ResultWithBom_IsStillRead()
        {
            AgentPaths paths = AgentHarness.Create("result-bom").Paths;
            File.WriteAllText(paths.UpdateResultPath, "{\"outcome\":\"success\"}", new UTF8Encoding(true));
            Assert.Equal("success", AgentUpdate.PendingResultMessage(paths)["status"]);
        }
    }
}
