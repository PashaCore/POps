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
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // update_progress: update_agent ile update_result arasındaki aşamalar (servis ve updater -> servis dosyası)
    [Collection(SharedStateCollection.Name)]
    public class UpdateProgressTests : SharedStateTestBase, IDisposable
    {
        private const string MsiName = "POps-Agent-9.9.9-win-x64.msi";
        private readonly List<Dictionary<string, object>> _stages = new List<Dictionary<string, object>>();
        private readonly Ed25519PrivateKeyParameters _key;
        private readonly byte[] _package = RandomNumberGenerator.GetBytes(4096);

        public UpdateProgressTests()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("progress-data");
            SecureStore.Dir = TestEnvironment.NewDir("progress-secure");
            var generator = new Ed25519KeyPairGenerator();
            generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = generator.GenerateKeyPair();
            _key = (Ed25519PrivateKeyParameters)pair.Private;
            AgentUpdate.TrustedKeyOverride = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
            AgentUpdate.InstalledVersionOverride = "0.1.22-alpha";
            AgentUpdate.LaunchOverride = (_, _, _) => true;
        }

        public void Dispose()
        {
            AgentUpdate.TrustedKeyOverride = null;
            AgentUpdate.InstalledVersionOverride = null;
            AgentUpdate.LaunchOverride = null;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
        }

        private sealed class Server : HttpMessageHandler
        {
            public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request);
        }

        private HttpClient Serving(byte[] body, HttpStatusCode status = HttpStatusCode.OK) => new HttpClient(new Server
        {
            Respond = _ => Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) }),
        });

        private string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private JsonElement Command(string version = "9.9.9", object[] artifacts = null, bool sign = true, string manifestOverride = null)
        {
            artifacts ??= new object[] { new { name = MsiName, sha256 = Sha(_package), size = _package.Length } };
            byte[] manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schema = "pops-manifest/1", version, tag = "v" + version, released_at = 1791200000, artifacts,
            }));
            var signer = new Ed25519Signer();
            signer.Init(true, _key);
            signer.BlockUpdate(manifest, 0, manifest.Length);
            string signature = Convert.ToBase64String(sign ? signer.GenerateSignature() : new byte[64]);
            string json = JsonSerializer.Serialize(new { action = "update_agent", manifest = manifestOverride ?? Convert.ToBase64String(manifest), manifest_sig = signature });
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        private Task<bool> Collect(Dictionary<string, object> message)
        {
            lock (_stages) _stages.Add(message);
            return Task.FromResult(true);
        }

        private Task Run(JsonElement command, HttpClient http) =>
            AgentUpdate.HandleUpdateCommandAsync(command, http, "https://pops.example", Collect);

        private string[] Stages() { lock (_stages) return _stages.Select(m => (string)m["stage"]).ToArray(); }

        private Dictionary<string, object> Stage(string stage) { lock (_stages) return Assert.Single(_stages, m => (string)m["stage"] == stage); }

        private void AssertWellFormed()
        {
            lock (_stages)
                foreach (var message in _stages)
                {
                    Assert.Equal("update_progress", message["type"]);
                    Assert.False(message.ContainsKey("status"));
                    Assert.DoesNotContain(message.Values, v => v == null);
                }
        }

        [Fact]
        public async Task GoodCommand_ReportsStagesInOrder()
        {
            await Run(Command(), Serving(_package));
            Assert.Equal(new[] { "received", "downloaded", "verified", "updater_started" }, Stages());
            Assert.False(Stage("received").ContainsKey("to_version"));
            Assert.Equal("9.9.9", Stage("downloaded")["to_version"]);
            Assert.Equal("9.9.9", Stage("verified")["to_version"]);
            Assert.Equal("9.9.9", Stage("updater_started")["to_version"]);
            AssertWellFormed();
        }

        public static IEnumerable<object[]> RejectCases() => new[]
        {
            new object[] { "no-manifest", new[] { "received", "rejected" }, "imzalı manifest yok" },
            new object[] { "not-base64", new[] { "received", "rejected" }, "base64" },
            new object[] { "bad-signature", new[] { "received", "rejected" }, "imzası geçersiz" },
            new object[] { "not-newer", new[] { "received", "rejected" }, "yeni değil" },
            new object[] { "two-msis", new[] { "received", "rejected" }, "tek bir ajan MSI" },
            new object[] { "bad-sha", new[] { "received", "rejected" }, "özet ya da boyut" },
            new object[] { "http-404", new[] { "received", "rejected" }, "HTTP 404" },
            new object[] { "size-mismatch", new[] { "received", "downloaded", "rejected" }, "uyuşmuyor" },
            new object[] { "updater-missing", new[] { "received", "downloaded", "verified", "rejected" }, "POpsUpdater kopyalanamıyor" },
            new object[] { "exception", new[] { "received", "rejected" }, "Güncelleme hazırlanamadı" },
        };

        [Theory]
        [MemberData(nameof(RejectCases))]
        public async Task EveryRejection_SendsExactlyOneRejected(string scenario, string[] expected, string reason)
        {
            HttpClient http = Serving(_package);
            JsonElement command = Command();
            switch (scenario)
            {
                case "no-manifest": command = JsonDocument.Parse("{\"action\":\"update_agent\"}").RootElement.Clone(); break;
                case "not-base64": command = Command(manifestOverride: "%%%"); break;
                case "bad-signature": command = Command(sign: false); break;
                case "not-newer": command = Command(version: "0.1.0"); break;
                case "two-msis":
                    command = Command(artifacts: new object[]
                    {
                        new { name = MsiName, sha256 = Sha(_package), size = _package.Length },
                        new { name = "POps-Agent-9.9.8-win-x64.msi", sha256 = Sha(_package), size = _package.Length },
                    });
                    break;
                case "bad-sha": command = Command(artifacts: new object[] { new { name = MsiName, sha256 = "abc", size = _package.Length } }); break;
                case "http-404": http = Serving(Array.Empty<byte>(), HttpStatusCode.NotFound); break;
                case "size-mismatch": http = Serving(_package.Take(100).ToArray()); break;
                case "updater-missing": AgentUpdate.LaunchOverride = null; break;   // test klasöründe POpsUpdater.deps.json yok
                case "exception":
                    http = new HttpClient(new Server { Respond = _ => throw new HttpRequestException("sunucuya ulaşılamadı") });
                    break;
            }

            await Run(command, http);
            Assert.Equal(expected, Stages());
            Assert.Contains(reason, (string)Stage("rejected")["detail"]);
            AssertWellFormed();
            if (scenario == "not-newer") Assert.Equal("0.1.0", Stage("rejected")["to_version"]);
        }

        [Fact]
        public async Task FreshLock_SendsIgnoredBusy_WithTheVersionInProgress_AndNoReceived()
        {
            File.WriteAllText(AgentUpdate.LockPath, "{\"from_version\":\"0.1.21-alpha\",\"to_version\":\"0.1.22-alpha\",\"started_at\":1791200000}");
            await Run(Command(), Serving(_package));
            Assert.Equal(new[] { "ignored_busy" }, Stages());
            Assert.Equal("0.1.22-alpha", Stage("ignored_busy")["to_version"]);
            Assert.Contains("update.lock", (string)Stage("ignored_busy")["detail"]);
            AssertWellFormed();
        }

        [Fact]
        public async Task SecondCommandWhilePreparing_SendsIgnoredBusy_AndNoReceived()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow = new HttpClient(new Server
            {
                Respond = async _ =>
                {
                    downloading.TrySetResult(true);
                    await release.Task;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_package) };
                },
            });
            Task first = Run(Command(), slow);
            await downloading.Task;
            await Run(Command(version: "9.9.10"), Serving(_package));
            release.SetResult(true);
            await first;

            Assert.Equal(new[] { "received", "ignored_busy", "downloaded", "verified", "updater_started" }, Stages());
            Assert.Equal("9.9.9", Stage("ignored_busy")["to_version"]);
            Assert.Contains("hazırlanıyor", (string)Stage("ignored_busy")["detail"]);
        }

        [Fact]
        public void ProgressMessage_DropsInvalidOptionalValues()
        {
            Dictionary<string, object> message = AgentUpdate.ProgressMessage("waiting_installer", "0.1.22 alpha!", 6, 5, "");
            Assert.Equal(new[] { "type", "stage" }, message.Keys);
            message = AgentUpdate.ProgressMessage("waiting_installer", "0.1.22-alpha", 2, 5, "msiexec 1618");
            Assert.Equal(2, message["attempt"]);
            Assert.Equal(5, message["of"]);
            Assert.Equal("0.1.22-alpha", message["to_version"]);
        }

        // ------------------------------------------------------------------ updater -> servis dosyası
        [Fact]
        public void ProgressFile_RoundTrip_AndOnlyUpdaterStages()
        {
            string path = AgentUpdate.ProgressPath;
            var record = new UpdateProgressRecord { Run = 1791200000, ToVersion = "0.1.22-alpha", Stage = UpdateProgressFile.WaitingInstaller, Attempt = 2, Of = 5, Detail = "msiexec 1618", At = 1791200123 };
            UpdateProgressFile.Write(path, record);
            Assert.False(File.Exists(path + ".tmp"));
            string json = File.ReadAllText(path);
            Assert.Contains("\"schema\":\"pops-update-progress/1\"", json);

            UpdateProgressRecord back = UpdateProgressFile.Read(path);
            Assert.Equal((1791200000L, "0.1.22-alpha", "waiting_installer", 2, 5, "msiexec 1618", 1791200123L),
                (back.Run, back.ToVersion, back.Stage, back.Attempt.Value, back.Of.Value, back.Detail, back.At));

            Assert.DoesNotContain("detail", UpdateProgressFile.Serialize(new UpdateProgressRecord { Run = 1, Stage = UpdateProgressFile.Installing }));
            Assert.Null(UpdateProgressFile.Parse("{\"schema\":\"pops-update-progress/1\",\"run\":1,\"stage\":\"received\"}"));
            Assert.Null(UpdateProgressFile.Parse("{\"schema\":\"x\",\"run\":1,\"stage\":\"installing\"}"));
            Assert.Null(UpdateProgressFile.Parse("{bozuk"));
            Assert.Null(UpdateProgressFile.Read(Path.Combine(AgentUpdate.DataDir, "yok.json")));
        }

        // ------------------------------------------------------------------ servis: iletici
        private readonly List<JsonElement> _sent = new List<JsonElement>();

        private Worker NewWorker(bool feature = true)
        {
            var worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-PROG",
                SendOverride = payload => { lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(payload)); return Task.FromResult(true); },
            };
            worker.OnCommandSocketOpened();
            string features = feature ? "[\"update_result_ack\",\"result_ack\",\"update_progress\"]" : "[\"result_ack\"]";
            worker.Handshake.OnServerInfo(JsonDocument.Parse("{\"action\":\"server_info\",\"features\":" + features + "}").RootElement);
            return worker;
        }

        private List<JsonElement> Forwarded() { lock (_sent) return _sent.Where(m => m.GetProperty("type").GetString() == "update_progress").ToList(); }

        private const long Run1 = 1791200000;

        private void Updating(long run = Run1, string stage = UpdateProgressFile.WaitingInstaller, int attempt = 2, long at = 1791200123)
        {
            File.WriteAllText(AgentUpdate.LockPath, "{\"from_version\":\"0.1.21-alpha\",\"to_version\":\"0.1.22-alpha\",\"started_at\":" + Run1 + "}");
            UpdateProgressFile.Write(AgentUpdate.ProgressPath, new UpdateProgressRecord
            {
                Run = run, ToVersion = "0.1.22-alpha", Stage = stage, Attempt = attempt, Of = 5, Detail = "msiexec 1618: başka bir Windows Installer kurulumu sürüyor", At = at,
            });
        }

        [Fact]
        public async Task NoStage_BeforeTheFirstHeartbeat()
        {
            using Worker worker = NewWorker();
            Assert.False(await worker.ReportUpdateProgressAsync(AgentUpdate.ProgressMessage("received")));
            Updating();
            await worker.ForwardUpdateProgressAsync();
            Assert.Empty(Forwarded());

            worker.OnHeartbeatSent();
            Assert.True(await worker.ReportUpdateProgressAsync(AgentUpdate.ProgressMessage("received")));
        }

        [Fact]
        public async Task OldServer_GetsNoStages()
        {
            using Worker worker = NewWorker(feature: false);
            worker.OnHeartbeatSent();
            Assert.False(await worker.ReportUpdateProgressAsync(AgentUpdate.ProgressMessage("received")));
            Assert.Empty(Forwarded());
        }

        [Fact]
        public async Task Forwarder_SendsOncePerChange_AndAgainAfterReconnect()
        {
            using Worker worker = NewWorker();
            worker.OnHeartbeatSent();
            Updating();
            await worker.ForwardUpdateProgressAsync();
            await worker.ForwardUpdateProgressAsync();
            JsonElement message = Assert.Single(Forwarded());
            Assert.Equal("waiting_installer", message.GetProperty("stage").GetString());
            Assert.Equal("0.1.22-alpha", message.GetProperty("to_version").GetString());
            Assert.Equal(2, message.GetProperty("attempt").GetInt32());
            Assert.Equal(5, message.GetProperty("of").GetInt32());
            Assert.False(message.TryGetProperty("status", out _));
            Assert.False(message.TryGetProperty("run", out _));

            Updating(attempt: 3, at: 1791200190);
            await worker.ForwardUpdateProgressAsync();
            Assert.Equal(2, Forwarded().Count);

            // Yeniden bağlandı: son aşama ilk heartbeat'ten (ve yeni server_info'dan) sonra bir kez daha gider
            worker.OnCommandSocketOpened();
            worker.Handshake.OnServerInfo(JsonDocument.Parse("{\"action\":\"server_info\",\"features\":[\"update_progress\"]}").RootElement);
            await worker.ForwardUpdateProgressAsync();
            Assert.Equal(2, Forwarded().Count);
            worker.OnHeartbeatSent();
            await worker.ForwardUpdateProgressAsync();
            await worker.ForwardUpdateProgressAsync();
            Assert.Equal(3, Forwarded().Count);
        }

        [Fact]
        public async Task Forwarder_IgnoresStaleLock_WrongRun_AndExistingResult()
        {
            using Worker worker = NewWorker();
            worker.OnHeartbeatSent();

            Updating(run: Run1 + 1);   // başka bir çalışmadan kalmış
            await worker.ForwardUpdateProgressAsync();
            Assert.Empty(Forwarded());

            Updating();
            File.WriteAllText(AgentUpdate.ResultPath, "{\"schema\":\"pops-update-result/1\"}");
            await worker.ForwardUpdateProgressAsync();
            Assert.Empty(Forwarded());
            File.Delete(AgentUpdate.ResultPath);

            Updating();
            File.SetLastWriteTimeUtc(AgentUpdate.LockPath, DateTime.UtcNow.AddHours(-1));   // kilit bayat
            await worker.ForwardUpdateProgressAsync();
            Assert.Empty(Forwarded());
            Assert.False(File.Exists(AgentUpdate.ProgressPath));
        }

        [Fact]
        public void Startup_RemovesALeftoverProgressFile_OnlyWhenNoUpdateRuns()
        {
            Updating();
            AgentUpdate.CleanupStaleProgress();
            Assert.True(File.Exists(AgentUpdate.ProgressPath));
            File.Delete(AgentUpdate.LockPath);
            AgentUpdate.CleanupStaleProgress();
            Assert.False(File.Exists(AgentUpdate.ProgressPath));
        }
    }
}
