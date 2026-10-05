using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // docs/protocol: sunucu ile ajanın ortak test vektörleri ve JSON şemaları (bkz. docs/protocol/AGENT_TESTS.md).
    // Dosyalar kopyalanmaz; iki taraf da aynı kopyayı okur.
    public class ProtocolVectorTests : TestBase, IDisposable
    {
        private readonly Worker _worker;
        private readonly List<JsonElement> _sent = new List<JsonElement>();

        public ProtocolVectorTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("protocol-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("protocol-data");
            AgentCapabilities.Load();
            _worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-3F9A1C7B2E4D",
                SendOverride = payload =>
                {
                    lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(payload));
                    return Task.FromResult(true);
                },
            };
            _worker.Binding = new HardwareBinding(AgentUpdate.IdentityPath, () => ("4C4C4544-0042-3510-8051-B3C04F4D3132", "B3C0MD2"));
            _worker.Quarantine = new QuarantineControl(_ => { },
                () => { File.WriteAllText(NetworkIsolation.StatePath, "{}"); return Task.FromResult(true); },
                () => { File.Delete(NetworkIsolation.StatePath); return Task.FromResult(true); });
        }

        public void Dispose()
        {
            _worker.Dispose();
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentCapabilities.Load();
        }

        private static string Protocol(params string[] parts) =>
            Path.Combine(new[] { TestEnvironment.RepoRoot(), "docs", "protocol" }.Concat(parts).ToArray());

        public static IEnumerable<object[]> ServerVectors() =>
            Directory.GetFiles(Protocol("examples", "server-to-agent"), "*.json").Select(p => new object[] { Path.GetFileName(p) });

        private List<JsonElement> Sent(string type = null)
        {
            lock (_sent) return _sent.Where(m => type == null || (m.TryGetProperty("type", out var t) && t.GetString() == type)).ToList();
        }

        private static void CapabilitiesOff()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":false,\"vision_enabled\":false}");
            AgentCapabilities.Load();
        }

        private static void ModulesOff() =>
            AgentModules.Apply(JsonDocument.Parse("{\"modules\":{\"patches\":false,\"wol\":false}}").RootElement);

        private static void PowerAndMessageOff()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"power_enabled\":false,\"message_enabled\":false}");
            AgentCapabilities.Load();
        }

        // ------------------------------------------------------------------ 1. sunucu -> ajan vektörleri
        [Theory]
        [MemberData(nameof(ServerVectors))]
        public async Task EveryServerVector_IsAccepted(string file)
        {
            string text = File.ReadAllText(Protocol("examples", "server-to-agent", file));
            JsonElement vector = JsonDocument.Parse(text).RootElement.Clone();
            string kind = file.Split('.')[0];
            if (kind is "power" or "user_message")
            {
                await PowerOrMessageVector(kind, text, vector.GetProperty("task_id").GetInt32());
                return;
            }
            if (kind is "execute" or "remote_input" or "start_stream" or "start_vision_session") CapabilitiesOff();
            if (kind is "scan_updates" or "install_updates" or "wake_peer") ModulesOff();
            if (kind is "update_agent")
            {
                // Ret, update_progress "rejected" ile görünür (sunucu update_progress'i bildirmiş, heartbeat gitmiş olmalı)
                await _worker.HandleServerMessageAsync(File.ReadAllText(Protocol("examples", "server-to-agent", "server_info.json")), null, CancellationToken.None);
                _worker.OnHeartbeatSent();
            }

            await _worker.HandleServerMessageAsync(text, null, CancellationToken.None);

            switch (kind)
            {
                case "server_info":
                    Assert.True(_worker.Handshake.Supports(ResultSpool.AckFeature));
                    Assert.True(_worker.Handshake.Supports(UpdateResultReporter.AckFeature));
                    break;
                case "set_secret":
                    Assert.Equal(vector.GetProperty("secret").GetString(), AgentCredentials.CurrentSecret);
                    break;
                case "set_identity":
                    Assert.Equal("HW-9B41D07E5A2C", _worker.HwId);
                    break;
                case "set_bypass_secret":
                    Assert.Empty(Sent());
                    Assert.True(BypassSecretCommand.Process(vector.GetProperty("secret").GetString(), true, _ => true, _ => { }, out string fingerprint));
                    Assert.Equal(Example("agent-to-server", "bypass_secret_ack.json").GetProperty("fingerprint").GetString(), fingerprint);
                    break;
                case "execute":
                    JsonElement result = Assert.Single(Sent("result"));
                    Assert.Equal(CommandRunner.ExitDenied, result.GetProperty("exit_code").GetInt32());
                    Assert.Equal(vector.GetProperty("task_id").GetInt32(), Assert.Single(Sent("capability_denied")).GetProperty("task_id").GetInt32());
                    break;
                case "cancel_task" or "result_ack" or "update_result_ack" or "unlock":
                    Assert.Empty(Sent());
                    break;
                case "lockdown":
                    Assert.True(File.Exists(NetworkIsolation.StatePath));
                    break;
                case "set_capabilities":
                    Assert.False(AgentCapabilities.TerminalEnabled);
                    if (vector.TryGetProperty("vision_enabled", out _)) Assert.False(AgentCapabilities.VisionEnabled);
                    Assert.Single(Sent("capabilities"));
                    break;
                case "remote_input" or "start_stream" or "start_vision_session":
                    Assert.Equal("vision", Assert.Single(Sent("capability_denied")).GetProperty("capability").GetString());
                    break;
                case "scan_updates" or "install_updates" or "wake_peer":
                    Assert.Equal("module_disabled", Assert.Single(Sent("capability_denied")).GetProperty("reason").GetString());
                    break;
                case "update_agent":
                    // Emir arka planda işlenir; test anahtarıyla imzalı manifest yayın anahtarıyla reddedilir
                    JsonElement rejected = await WaitFor(m => m.GetProperty("type").GetString() == "update_progress" && m.GetProperty("stage").GetString() == "rejected");
                    Assert.Contains("imza", rejected.GetProperty("detail").GetString(), StringComparison.Ordinal);
                    Assert.Empty(Sent("update_result"));
                    break;
            }
        }

        // power*.json ve user_message.json (AGENT_TESTS.md): bilgisayar kapanmaz, kilitlenmez, gerçek pencere açılmaz.
        // Güç vektörü yalnızca yerel yetenek kapalıyken ya da (logoff/lock) oturum açık kullanıcı yokken işlenir;
        // TestEnvironment güç API'sinin yerine her çağrıda hata veren sahteyi ve "konsolda kullanıcı yok"u koyar. Mesajı
        // borudaki tepsi yerine TrayOverride alır; Olay Günlüğüne yazılmaz.
        private async Task PowerOrMessageVector(string kind, string text, int taskId)
        {
            List<JsonElement> Results() => Sent("result").Where(m => m.GetProperty("task_id").GetInt32() == taskId).ToList();
            void Clear() { lock (_sent) _sent.Clear(); }
            _worker.Power.Audit = _ => { };
            _worker.Messages.Audit = _ => { };
            string op = kind == "power" ? JsonDocument.Parse(text).RootElement.GetProperty("op").GetString() : null;

            if (kind == "user_message" || op is "logoff" or "lock")
            {
                // Oturum açık kullanıcı yok (tepsi de bağlı değil): -6
                await _worker.HandleServerMessageAsync(text, null, CancellationToken.None);
                JsonElement noUser = Assert.Single(Results());
                Assert.Equal(SessionTasks.ExitNoUser, noUser.GetProperty("exit_code").GetInt32());
                Assert.StartsWith("[REDDEDİLDİ]", noUser.GetProperty("output").GetString());
                Assert.Empty(Sent("capability_denied"));
                Assert.Null(_worker.Power.CurrentTaskId);
                Clear();
            }
            if (kind == "user_message")
            {
                // Okundu onayı veren sahte tepsi: tek sonuç, "[TAMAM] okundu"
                _worker.TrayConnectedOverride = () => true;
                _worker.TrayOverride = message =>
                {
                    if (message.StartsWith(UserMessages.ShowPrefix, StringComparison.Ordinal))
                        _ = _worker.OnTrayReplyAsync(UserMessages.AckPrefix + taskId.ToString(CultureInfo.InvariantCulture));
                };
                await _worker.HandleServerMessageAsync(text, null, CancellationToken.None);
                JsonElement read = await WaitFor(m => m.GetProperty("type").GetString() == "result");
                Assert.Equal(0, read.GetProperty("exit_code").GetInt32());
                Assert.Equal("[TAMAM] okundu", read.GetProperty("output").GetString());
                Assert.False(_worker.Messages.IsWaiting(taskId));
                Assert.Single(Results());
                Clear();
            }

            // Yerel yetenek kapalı: -5 ve capability_denied (capability power / message)
            PowerAndMessageOff();
            await _worker.HandleServerMessageAsync(text, null, CancellationToken.None);
            JsonElement refused = Assert.Single(Results());
            Assert.Equal(CommandRunner.ExitDenied, refused.GetProperty("exit_code").GetInt32());
            Assert.StartsWith("[REDDEDİLDİ]", refused.GetProperty("output").GetString());
            JsonElement denied = Assert.Single(Sent("capability_denied"));
            Assert.Equal(kind == "power" ? "power" : "message", denied.GetProperty("capability").GetString());
            Assert.Equal(kind, denied.GetProperty("action").GetString());
            Assert.Equal(taskId, denied.GetProperty("task_id").GetInt32());
            AssertMatches("result", refused);
            AssertMatches("capability_denied", denied);
            Assert.Null(_worker.Power.CurrentTaskId);
        }

        private async Task<JsonElement> WaitFor(Func<JsonElement, bool> match)
        {
            for (int i = 0; i < 200; i++)
            {
                JsonElement found = Sent().FirstOrDefault(match);
                if (found.ValueKind != JsonValueKind.Undefined) return found;
                await Task.Delay(50);
            }
            throw new TimeoutException("beklenen mesaj 10 sn içinde gönderilmedi");
        }

        [Fact]
        public async Task UnknownServerMessage_IsIgnored()
        {
            await _worker.HandleServerMessageAsync(File.ReadAllText(Protocol("examples", "unknown", "server-to-agent.json")), null, CancellationToken.None);
            Assert.Empty(Sent());
        }

        // ------------------------------------------------------------------ 2. ajanın ürettikleri şemaya uyar
        private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new ConcurrentDictionary<string, JsonSchema>();

        private static JsonSchema Schema(string type) =>
            Schemas.GetOrAdd(type, t => JsonSchema.FromFile(Protocol("agent-to-server", t + ".json"), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));

        private static JsonElement SchemaJson(string type) => JsonDocument.Parse(File.ReadAllText(Protocol("agent-to-server", type + ".json"))).RootElement.Clone();

        private static JsonElement Example(string direction, string file) => JsonDocument.Parse(File.ReadAllText(Protocol("examples", direction, file))).RootElement.Clone();

        private static void AssertMatches(string type, JsonElement message)
        {
            EvaluationResults result = Schema(type).Evaluate(message, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, $"{type} şemaya uymuyor: " + string.Join("; ", (result.Details ?? new List<EvaluationResults>())
                .Where(d => d.Errors != null).SelectMany(d => d.Errors.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
            Assert.Empty(Undocumented(SchemaJson(type), message));
        }

        // Ajanın gönderdiği her alan şemada belgelenmiş olmalı (iç içe nesneler dahil)
        private static IEnumerable<string> Undocumented(JsonElement schema, JsonElement message, string prefix = "")
        {
            if (message.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out JsonElement props)) yield break;
            foreach (JsonProperty p in message.EnumerateObject())
            {
                if (!props.TryGetProperty(p.Name, out JsonElement sub)) { yield return prefix + p.Name; continue; }
                foreach (string inner in Undocumented(sub, p.Value, prefix + p.Name + ".")) yield return inner;
            }
        }

        private static object SampleDna() => new
        {
            os = "Microsoft Windows 11 Pro",
            capabilities = new { ram_readable = true, disk_serial_real = true, wmi_healthy = true },
            hardware = new { uuid = "4c4c4544-0042-3510-8051-b3c04f4d3132", bios_sn = "B3C0MD2", disk_sn = "S5GXNX0T612345", mac = "A4:BB:6D:12:34:56", ram_sn = "8A1B2C3D" },
        };

        private JsonElement Heartbeat(bool withDna)
        {
            typeof(Worker).GetField("_cachedDna", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_worker, withDna ? SampleDna() : null);
            return JsonSerializer.SerializeToElement(_worker.HeartbeatPayload());
        }

        [Fact]
        public void Heartbeat_MatchesItsSchema_WithAndWithoutDna()
        {
            AssertMatches("heartbeat", Heartbeat(withDna: true));
            AssertMatches("heartbeat", Heartbeat(withDna: false));
        }

        [Fact]
        public void Capabilities_MatchItsSchema() => AssertMatches("capabilities", JsonSerializer.SerializeToElement(AgentCapabilities.StatusMessage()));

        private async Task<(JsonElement Result, JsonElement Denied)> RefusedExecute()
        {
            AgentModules.Apply(JsonDocument.Parse("{\"modules\":{\"terminal\":false}}").RootElement);
            await _worker.HandleServerMessageAsync(File.ReadAllText(Protocol("examples", "server-to-agent", "execute.queue.json")), null, CancellationToken.None);
            return (Assert.Single(Sent("result")), Assert.Single(Sent("capability_denied")));
        }

        [Fact]
        public async Task RefusedExecute_MatchesTheSchemas()
        {
            var (result, denied) = await RefusedExecute();
            AssertMatches("result", result);
            AssertMatches("capability_denied", denied);
        }

        private static JsonElement UpdateResult()
        {
            Directory.CreateDirectory(AgentUpdate.DataDir);
            File.WriteAllText(AgentUpdate.ResultPath, JsonSerializer.Serialize(new
            {
                schema = "pops-update-result/1", from_version = "0.1.20-alpha", to_version = "0.1.21-alpha", started_at = 1791200000,
                rollback = "none", msi_exit_code = 0, reboot_required = false, agent_state = "running", running_version = "0.1.21-alpha",
                outcome = "success", finished_at = 1791200120,
            }));
            return JsonSerializer.SerializeToElement(AgentUpdate.PendingResultMessage());
        }

        [Fact]
        public void UpdateResult_MatchesItsSchema() => AssertMatches("update_result", UpdateResult());

        private static JsonElement Progress() =>
            JsonSerializer.SerializeToElement(AgentUpdate.ProgressMessage("waiting_installer", "0.1.22-alpha", 2, 5, "msiexec 1618: başka bir Windows Installer kurulumu sürüyor"));

        [Fact]
        public void UpdateProgress_MatchesItsSchema() => AssertMatches("update_progress", Progress());

        // ------------------------------------------------------------------ 3. imzalı güncelleme vektörü
        [Fact]
        public void SignedUpdateVector_VerifiesWithTheTestKeyOnly()
        {
            JsonElement vector = Example("server-to-agent", "update_agent.json");
            byte[] manifest = Convert.FromBase64String(vector.GetProperty("manifest").GetString());
            string signature = vector.GetProperty("manifest_sig").GetString();
            Assert.True(ReleaseVerifier.VerifySignature(manifest, signature, ReleaseVerifierTests.TestPublicKey));
            Assert.False(ReleaseVerifier.VerifySignature(manifest, signature));
            ReleaseVerifier.Manifest parsed = ReleaseVerifier.Parse(manifest);
            Assert.Equal("0.1.3-alpha", parsed.Version);
            Assert.Single(parsed.Artifacts, a => a.Name.EndsWith(".msi", StringComparison.Ordinal));
            Assert.Equal(File.ReadAllBytes(TestEnvironment.TestData("manifest.json")), manifest);
        }

        // ------------------------------------------------------------------ 4. ajan -> sunucu örnekleri ajanla aynı
        private static string[] Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        [Fact]
        public async Task AgentExamples_HaveTheKeysTheAgentSends()
        {
            Assert.Equal(Keys(Example("agent-to-server", "heartbeat.first.json")), Keys(Heartbeat(withDna: true)));
            // Bugünkü ajan files_enabled, exam_enabled, power_enabled ve message_enabled da bildirir;
            // capabilities.default.json daha eski ajanların (ve Linux ajanının) iletisidir
            Assert.Equal(Keys(Example("agent-to-server", "capabilities.files.json")), Keys(JsonSerializer.SerializeToElement(AgentCapabilities.StatusMessage())));
            Assert.Equal(Keys(Example("agent-to-server", "update_result.success.json")), Keys(UpdateResult()));
            Assert.Equal(Keys(Example("agent-to-server", "update_progress.json")), Keys(Progress()));
            var (result, denied) = await RefusedExecute();
            Assert.Equal(Keys(Example("agent-to-server", "result.denied.json")), Keys(result));
            Assert.Equal(Keys(Example("agent-to-server", "capability_denied.execute.json")), Keys(denied));
        }
    }
}
