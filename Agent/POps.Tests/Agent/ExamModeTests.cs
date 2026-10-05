using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Sınav modu: emrin doğrulanması, güvenlik duvarı kuralları (ayrı grup), süresinde bitmesi, uygulama engeli
    [Collection(SharedStateCollection.Name)]
    public class ExamModeTests : SharedStateTestBase, IDisposable
    {
        private readonly List<string> _scripts = new List<string>();
        private readonly Func<string, Task<(int, string)>> _runner = NetworkIsolation.ScriptRunner;
        private readonly Func<string, Task<IPAddress[]>> _resolver = ExamMode.Resolver;
        private IPAddress[] _resolved = { IPAddress.Parse("198.51.100.7") };
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

        public ExamModeTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("exam-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("exam-data");
            AgentCapabilities.Load();
            NetworkIsolation.ScriptRunner = script =>
            {
                lock (_scripts) _scripts.Add(script);
                return Task.FromResult((0, "[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Public\",\"Enabled\":\"False\"}]"));
            };
            ExamMode.Resolver = _ => Task.FromResult(_resolved);
        }

        public void Dispose()
        {
            NetworkIsolation.ScriptRunner = _runner;
            ExamMode.Resolver = _resolver;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentCapabilities.Load();
        }

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        private static string Command(long until) =>
            "{\"action\":\"exam_mode\",\"enabled\":true,\"allow\":[\"Sinav.MEB.gov.tr.\",\"10.0.0.5\",\"10.1.0.9/24\"],\"until\":" + until +
            ",\"message\":\"Sınav modu: yalnızca sınav sitesi açık\\u0007\",\"block_apps\":[\"cmd.exe\",\"Explorer.exe\",\"powershell.exe\"]}";

        [Fact]
        public void Command_IsValidatedAndNormalized()
        {
            Assert.True(ExamMode.TryParse(Json(Command(Now.ToUnixTimeSeconds() + 3600)), Now, out ExamSettings s, out string error), error);
            Assert.Equal(new[] { "sinav.meb.gov.tr", "10.0.0.5", "10.1.0.9/24" }, s.Allow);
            Assert.Equal(Now.ToUnixTimeSeconds() + 3600, s.Until);
            Assert.Equal(new[] { "cmd.exe", "powershell.exe" }, s.BlockApps);   // korunan süreç (explorer) düşer
            Assert.DoesNotContain('\u0007', s.Message);
            Assert.Equal(Now.ToUnixTimeSeconds(), s.Since);
        }

        [Theory]
        [InlineData("{\"allow\":[\"http://site.example\"]}", "geçersiz")]
        [InlineData("{\"allow\":[\"*.site.example\"]}", "geçersiz")]
        [InlineData("{\"allow\":[\"a b\"]}", "geçersiz")]
        [InlineData("{\"allow\":[\"10.0.0.0/4\"]}", "geçersiz")]
        [InlineData("{\"until\":1000}", "geçmişte")]
        [InlineData("{\"block_apps\":[\"cmd\"]}", "uygulama")]
        [InlineData("{\"block_apps\":[\"..\\\\x.exe\"]}", "uygulama")]
        public void InvalidCommands_AreRefused(string json, string expected)
        {
            Assert.False(ExamMode.TryParse(Json(json), Now, out _, out string error));
            Assert.Contains(expected, error);
        }

        [Fact]
        public void TooManyAllowEntries_AreRefused()
        {
            Assert.Equal(50, ExamMode.MaxAllow);   // sunucu şeması: maxItems 50
            string json = JsonSerializer.Serialize(new { allow = Enumerable.Range(1, ExamMode.MaxAllow).Select(i => $"10.0.{i / 250}.{i % 250 + 1}") });
            Assert.True(ExamMode.TryParse(Json(json), Now, out _, out string error), error);
            json = JsonSerializer.Serialize(new { allow = Enumerable.Range(1, ExamMode.MaxAllow + 1).Select(i => $"10.0.{i / 250}.{i % 250 + 1}") });
            Assert.False(ExamMode.TryParse(Json(json), Now, out _, out error));
            Assert.Contains("en çok", error);
        }

        [Fact]
        public void TooManyBlockedApps_AreRefused()
        {
            Assert.Equal(50, ExamMode.MaxApps);   // sunucu şeması: maxItems 50
            string json = JsonSerializer.Serialize(new { block_apps = Enumerable.Range(1, ExamMode.MaxApps).Select(i => $"app{i}.exe") });
            Assert.True(ExamMode.TryParse(Json(json), Now, out ExamSettings s, out string error), error);
            Assert.Equal(50, s.BlockApps.Count);
            json = JsonSerializer.Serialize(new { block_apps = Enumerable.Range(1, ExamMode.MaxApps + 1).Select(i => $"app{i}.exe") });
            Assert.False(ExamMode.TryParse(Json(json), Now, out _, out error));
            Assert.Contains("en çok", error);
        }

        [Fact]
        public void Cidr_IsAlignedToItsNetwork()
        {
            Assert.True(ExamMode.TryParseCidr("10.1.0.9/24", out var range));
            Assert.Equal("10.1.0.0-10.1.0.255", Range(range));
            Assert.True(ExamMode.TryParseCidr("2001:db8::1/120", out range));
            Assert.True(range.V6);
            Assert.False(ExamMode.TryParseCidr("10.0.0.0/33", out _));
        }

        private static string Range((System.Numerics.BigInteger Start, System.Numerics.BigInteger End, bool V6) r)
        {
            string Ip(System.Numerics.BigInteger v) => new IPAddress(v.ToByteArray(isUnsigned: true, isBigEndian: true).Reverse().Concat(new byte[4]).Take(4).Reverse().ToArray()).ToString();
            return $"{Ip(r.Start)}-{Ip(r.End)}";
        }

        [Fact]
        public async Task Enable_UsesItsOwnRuleGroup_AndKeepsTheServerAllowListAndRanges()
        {
            Assert.True(ExamMode.TryParse(Json(Command(Now.ToUnixTimeSeconds() + 3600)), Now, out ExamSettings s, out _));
            Assert.True(await ExamMode.EnableAsync(s, "https://203.0.113.10"));
            string script = Assert.Single(_scripts);
            Assert.Contains("$group = 'POps Exam'", script);
            Assert.Contains("'POps Exam - Outbound'", script);
            Assert.DoesNotContain("'203.0.113.10'", script);           // sunucu
            Assert.DoesNotContain("'198.51.100.7'", script);           // çözülen alan adı
            Assert.DoesNotContain("'10.0.0.5'", script);               // IP
            Assert.Contains("'10.0.0.6-10.0.255.255'", script);        // 10.1.0.0/24'ten önce engel biter...
            Assert.Contains("'10.1.1.0-", script);                     // ...ve sonra yeniden başlar
            Assert.True(ExamMode.IsActive);
            ExamSettings saved = ExamMode.Load();
            Assert.Contains("198.51.100.7", saved.Addresses);
            Assert.Equal(new[] { "Public" }, saved.PreviousDisabledProfiles);
        }

        [Fact]
        public async Task ServerAddressUnknown_NothingIsApplied()
        {
            Assert.True(ExamMode.TryParse(Json(Command(Now.ToUnixTimeSeconds() + 3600)), Now, out ExamSettings s, out _));
            Assert.False(await ExamMode.EnableAsync(s, "bozuk adres"));
            Assert.Empty(_scripts);
            Assert.False(ExamMode.IsActive);
        }

        [Fact]
        public async Task Refresh_RebuildsRulesOnlyWhenAddressesChange()
        {
            Assert.True(ExamMode.TryParse(Json(Command(Now.ToUnixTimeSeconds() + 3600)), Now, out ExamSettings s, out _));
            await ExamMode.EnableAsync(s, "https://203.0.113.10");
            Assert.False(await ExamMode.RefreshAsync("https://203.0.113.10", "test"));
            Assert.Single(_scripts);
            _resolved = new[] { IPAddress.Parse("198.51.100.8") };
            Assert.True(await ExamMode.RefreshAsync("https://203.0.113.10", "test"));
            Assert.Equal(2, _scripts.Count);
            Assert.Contains("198.51.100.8", ExamMode.Load().Addresses);
            Assert.Equal(new[] { "Public" }, ExamMode.Load().PreviousDisabledProfiles);
        }

        [Fact]
        public async Task Disable_RestoresProfilesOnlyIfQuarantineIsNotRunning()
        {
            Assert.True(ExamMode.TryParse(Json(Command(Now.ToUnixTimeSeconds() + 3600)), Now, out ExamSettings s, out _));
            await ExamMode.EnableAsync(s, "https://203.0.113.10");
            Assert.True(await ExamMode.DisableAsync());
            string script = _scripts.Last();
            Assert.Contains("Get-NetFirewallRule -Group 'POps Exam' -ErrorAction SilentlyContinue | Remove-NetFirewallRule", script);
            Assert.DoesNotContain("POps_Isolation_*", script);
            Assert.Contains("if (@(Get-NetFirewallRule -Group 'POps Isolation'", script);
            Assert.Contains("Set-NetFirewallProfile -Profile Public -Enabled False", script);
            Assert.False(ExamMode.IsActive);

            // Karantina kalkarken sınav sürüyorsa profiller açık kalır
            Assert.Contains("if (@(Get-NetFirewallRule -Group 'POps Exam'", NetworkIsolation.BuildDisableScript(new[] { "Public" }));
        }

        [Fact]
        public void ProcessesToStop_OnlyUserSessions_AndNeverProtected()
        {
            var processes = new[] { (1, "CMD", 1), (2, "cmd", 0), (3, "notepad", 1), (4, "explorer", 1), (5, "powershell", 2) };
            Assert.Equal(new[] { 1, 5 }, ExamMode.ProcessesToStop(processes, new[] { "cmd.exe", "powershell.exe", "explorer.exe" }));
        }

        // ------------------------------------------------------------------ servis
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private readonly List<string> _tray = new List<string>();

        private Worker NewWorker(bool online = true) => new Worker(NullLogger<Worker>.Instance)
        {
            HwId = "HW-EXAM",
            SendOverride = p => { lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(p)); return Task.FromResult(online); },
            TrayOverride = m => { lock (_tray) _tray.Add(m); },
        };

        private JsonElement LastState() { lock (_sent) return _sent.Last(m => m.GetProperty("type").GetString() == "exam_state"); }

        [Fact]
        public async Task ExamCommand_AppliesAndReports_ThenEnds()
        {
            using Worker worker = NewWorker();
            long until = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
            await worker.HandleExamModeAsync(Json(Command(until)));
            JsonElement state = LastState();
            AssertExamStateShape(state);
            Assert.True(state.GetProperty("enabled").GetBoolean());
            Assert.Equal(until, state.GetProperty("until").GetInt64());
            Assert.True(state.TryGetProperty("since", out _));
            Assert.False(state.TryGetProperty("status", out _));
            string banner = Assert.Single(_tray);
            Assert.StartsWith("EXAM_ON:", banner);
            using (JsonDocument doc = JsonDocument.Parse(Convert.FromBase64String(banner.Substring(8))))
            {
                Assert.Contains("yalnızca sınav sitesi", doc.RootElement.GetProperty("message").GetString());
                Assert.Equal(until, doc.RootElement.GetProperty("until").GetInt64());
            }

            await worker.HandleExamModeAsync(Json("{\"action\":\"exam_mode\",\"enabled\":false}"));
            Assert.False(LastState().GetProperty("enabled").GetBoolean());
            Assert.Equal("EXAM_OFF", _tray.Last());
            Assert.False(ExamMode.IsActive);
        }

        [Fact]
        public async Task LocallyDisabledCapability_RefusesTheExam()
        {
            DisableExamCapabilityLocally();
            using Worker worker = NewWorker();
            await worker.HandleExamModeAsync(Json(Command(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600)));
            Assert.Empty(_scripts);
            Assert.False(ExamMode.IsActive);
            lock (_sent) Assert.Equal("capability_denied", Assert.Single(_sent).GetProperty("type").GetString());
            // capabilities mesajı sınav yeteneğinin kapalı olduğunu bildirir (exam_enabled, şemada isteğe bağlı)
            Assert.Equal(false, AgentCapabilities.StatusMessage()[AgentCapabilities.Exam]);
        }

        // Geçersiz emir: uygulanmaz, neden yalnızca yerel loga yazılır; sunucuya şemadaki alanlarla o anki durum gider
        [Fact]
        public async Task InvalidCommand_IsNotApplied_AndTheStateIsReported()
        {
            using Worker worker = NewWorker();
            await worker.HandleExamModeAsync(Json("{\"action\":\"exam_mode\",\"enabled\":true,\"allow\":[\"bad entry\"]}"));
            Assert.Empty(_scripts);
            JsonElement state = LastState();
            AssertExamStateShape(state);
            Assert.False(state.GetProperty("enabled").GetBoolean());
            Assert.False(state.TryGetProperty("detail", out _));
            Assert.False(state.TryGetProperty("since", out _));   // bu çalışmada sınavdan çıkılmadı
            Assert.Equal(JsonValueKind.Null, state.GetProperty("until").ValueKind);
        }

        // ------------------------------------------------------------------ sunucunun ortak test vektörleri
        // Sunucunun docs/protocol/examples dosyalarının metni: exam_mode.json, exam_mode.off.json, exam_state.json,
        // exam_state.off.json, capability_denied.exam.json, server_info.json, server_info.0_1_21.json
        private const string VectorExamMode = @"{
  ""action"": ""exam_mode"",
  ""enabled"": true,
  ""allow"": [
    ""sinav.meb.gov.tr"",
    ""10.0.0.5"",
    ""10.1.0.0/24""
  ],
  ""until"": 1791207689,
  ""message"": ""Sınav modu: yalnızca sınav sitesi açık"",
  ""block_apps"": [
    ""cmd.exe"",
    ""powershell.exe""
  ]
}";
        private const string VectorExamModeOff = @"{
  ""action"": ""exam_mode"",
  ""enabled"": false
}";
        private const string VectorExamState = @"{
  ""type"": ""exam_state"",
  ""enabled"": true,
  ""since"": 1791205289,
  ""until"": 1791207689
}";
        private const string VectorExamStateOff = @"{
  ""type"": ""exam_state"",
  ""enabled"": false,
  ""since"": 1791205409,
  ""until"": null
}";
        private const string VectorCapabilityDeniedExam = @"{
  ""type"": ""capability_denied"",
  ""capability"": ""exam"",
  ""action"": ""exam_mode""
}";
        private const string VectorServerInfo = @"{
  ""action"": ""server_info"",
  ""version"": ""0.1.23-alpha"",
  ""protocol"": 1,
  ""features"": [
    ""update_result_ack"",
    ""result_ack"",
    ""update_progress"",
    ""file_transfer"",
    ""exam_mode"",
    ""winget"",
    ""vision_binary"",
    ""vision_clipboard""
  ]
}";
        private const string VectorServerInfoOld = @"{
  ""action"": ""server_info"",
  ""version"": ""0.1.21-alpha"",
  ""features"": [
    ""update_result_ack"",
    ""result_ack""
  ]
}";

        // Vektördeki until (2026-10-05 13:41:29Z) sabittir: emir, örnek exam_state'in since anında gelmiş sayılır
        private static readonly DateTimeOffset VectorNow = DateTimeOffset.FromUnixTimeSeconds(1791205289);

        private static Task Handle(Worker worker, string json) => worker.HandleServerMessageAsync(json, null, CancellationToken.None);

        private List<JsonElement> Sent(string type) { lock (_sent) return _sent.Where(m => m.TryGetProperty("type", out JsonElement t) && t.GetString() == type).ToList(); }

        private static void DisableExamCapabilityLocally()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":true,\"vision_enabled\":true,\"exam_enabled\":false}");
            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.ExamEnabled);
        }

        // Sunucunun exam_state şeması: type ve enabled zorunlu; since ve until sayı ya da null; başka alan yok
        private static void AssertExamStateShape(JsonElement state)
        {
            Assert.All(state.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "type", "enabled", "since", "until" }));
            Assert.Equal("exam_state", state.GetProperty("type").GetString());
            Assert.True(state.GetProperty("enabled").ValueKind is JsonValueKind.True or JsonValueKind.False);
            foreach (string name in new[] { "since", "until" })
                if (state.TryGetProperty(name, out JsonElement value)) Assert.True(value.ValueKind is JsonValueKind.Number or JsonValueKind.Null, name);
        }

        private static void AssertSameJson(string expected, JsonElement actual) =>
            Assert.Equal(JsonSerializer.Serialize(Json(expected)), JsonSerializer.Serialize(actual));

        [Fact]
        public async Task Vector_ExamMode_IsApplied_AndReportedAsTheExampleState()
        {
            using Worker worker = NewWorker();
            worker.ExamClock = () => VectorNow;
            await Handle(worker, VectorExamMode);
            Assert.True(ExamMode.IsActive);
            ExamSettings applied = ExamMode.Load();
            Assert.Equal(new[] { "sinav.meb.gov.tr", "10.0.0.5", "10.1.0.0/24" }, applied.Allow);
            Assert.Equal("Sınav modu: yalnızca sınav sitesi açık", applied.Message);
            Assert.Equal(new[] { "cmd.exe", "powershell.exe" }, applied.BlockApps);
            Assert.Equal(1791207689, applied.Until);
            JsonElement state = Assert.Single(Sent("exam_state"));
            AssertExamStateShape(state);
            AssertSameJson(VectorExamState, state);

            // exam_mode.off.json: sınav kalkar, exam_state enabled:false (since: çıkış anı, until: null)
            worker.ExamClock = () => DateTimeOffset.FromUnixTimeSeconds(1791205409);
            await Handle(worker, VectorExamModeOff);
            Assert.False(ExamMode.IsActive);
            Assert.Contains("Get-NetFirewallRule -Group 'POps Exam' -ErrorAction SilentlyContinue | Remove-NetFirewallRule", _scripts.Last());
            state = LastState();
            AssertExamStateShape(state);
            AssertSameJson(VectorExamStateOff, state);
        }

        [Fact]
        public async Task Vector_ExamMode_WithTheCapabilityOff_IsDeniedOnce_AndNothingIsApplied()
        {
            DisableExamCapabilityLocally();
            using Worker worker = NewWorker();
            worker.ExamClock = () => VectorNow;
            await Handle(worker, VectorExamMode);
            Assert.Empty(_scripts);
            Assert.False(ExamMode.IsActive);
            Assert.Empty(_tray);
            JsonElement denied;
            lock (_sent) denied = Assert.Single(_sent);
            AssertSameJson(VectorCapabilityDeniedExam, denied);
        }

        [Fact]
        public async Task Vector_ExamModeOff_WithoutAnExam_ChangesNothing()
        {
            using Worker worker = NewWorker();
            await Handle(worker, VectorExamModeOff);
            Assert.Empty(_scripts);
            Assert.Empty(_tray);
            JsonElement state = Assert.Single(Sent("exam_state"));
            AssertExamStateShape(state);
            Assert.False(state.GetProperty("enabled").GetBoolean());
        }

        // Her bağlantıda server_info'dan sonra bir kez; yalnızca exam_mode duyuran sunucuya; ilk mesaj olarak asla
        [Fact]
        public async Task State_IsReportedOncePerConnection_AfterServerInfo_ToServersWithExamMode()
        {
            using Worker worker = NewWorker();
            worker.OnCommandSocketOpened();
            await worker.ReportExamStateOnConnectAsync();   // server_info henüz yok
            Assert.Empty(Sent("exam_state"));

            await Handle(worker, VectorServerInfo);
            JsonElement state = Assert.Single(Sent("exam_state"));
            AssertExamStateShape(state);
            Assert.False(state.GetProperty("enabled").GetBoolean());
            Assert.False(state.TryGetProperty("since", out _));
            await Handle(worker, VectorServerInfo);          // aynı bağlantıda ikinci kez gönderilmez
            Assert.Single(Sent("exam_state"));

            // Yeni bağlantı: yeniden; sınavdayken giriş zamanı ve bitişle
            worker.ExamClock = () => VectorNow;
            await worker.HandleExamModeAsync(Json(VectorExamMode));
            worker.OnCommandSocketOpened();
            await Handle(worker, VectorServerInfo);
            Assert.Equal(3, Sent("exam_state").Count);
            AssertSameJson(VectorExamState, LastState());

            // exam_mode duyurmayan (eski) sunucu: gönderilmez
            worker.OnCommandSocketOpened();
            await Handle(worker, VectorServerInfoOld);
            Assert.Equal(3, Sent("exam_state").Count);
        }

        // Süre dolması gibi kendiliğinden değişiklik server_info gelmeden gönderilmez (bağlantı sonrası bildirim taşır)
        [Fact]
        public async Task SpontaneousChange_WaitsForServerInfo()
        {
            using Worker worker = NewWorker();
            long until = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
            await worker.HandleExamModeAsync(Json(Command(until)));
            int before = Sent("exam_state").Count;
            worker.OnCommandSocketOpened();
            Assert.True(await worker.ExamTickAsync(DateTimeOffset.FromUnixTimeSeconds(until), null));
            Assert.Equal(before, Sent("exam_state").Count);

            await Handle(worker, VectorServerInfo);
            JsonElement state = LastState();
            Assert.Equal(before + 1, Sent("exam_state").Count);
            Assert.False(state.GetProperty("enabled").GetBoolean());
            Assert.True(state.TryGetProperty("since", out JsonElement since) && since.GetInt64() > 0);   // çıkış anı
            Assert.Equal(JsonValueKind.Null, state.GetProperty("until").ValueKind);
        }

        // Sınav sürerken yetenek yerelde kapandı (EXAM_ENABLED=0 ile yeniden kurulum): dönemsel turda sınav biter
        [Fact]
        public async Task CapabilityOffLocally_EndsTheRunningExam_OnTheNextTick()
        {
            using Worker worker = NewWorker();
            await Handle(worker, VectorServerInfo);
            await worker.HandleExamModeAsync(Json(Command(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600)));
            Assert.True(ExamMode.IsActive);
            DisableExamCapabilityLocally();

            Assert.True(await worker.ExamTickAsync(DateTimeOffset.UtcNow, null));
            Assert.False(ExamMode.IsActive);
            Assert.Equal("EXAM_OFF", _tray.Last());
            JsonElement state = LastState();
            AssertExamStateShape(state);
            Assert.False(state.GetProperty("enabled").GetBoolean());
        }

        // Sınav sürerken sunucu set_capabilities ile sınav yeteneğini kapattı: capabilities ve hemen ardından sınav biter
        [Fact]
        public async Task SetCapabilitiesExamOff_EndsTheRunningExam()
        {
            using Worker worker = NewWorker();
            await Handle(worker, VectorServerInfo);
            await worker.HandleExamModeAsync(Json(Command(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600)));
            Assert.True(ExamMode.IsActive);

            await Handle(worker, "{\"action\":\"set_capabilities\",\"exam_enabled\":false}");
            Assert.False(AgentCapabilities.ExamEnabled);
            Assert.False(ExamMode.IsActive);
            JsonElement capabilities = Assert.Single(Sent("capabilities"));
            Assert.False(capabilities.GetProperty("exam_enabled").GetBoolean());
            Assert.False(LastState().GetProperty("enabled").GetBoolean());

            // Ardından gelen exam_mode reddedilir, uygulanmaz
            int scripts = _scripts.Count;
            await worker.HandleExamModeAsync(Json(Command(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600)));
            Assert.Equal(scripts, _scripts.Count);
            Assert.Single(Sent("capability_denied"));
        }

        [Fact]
        public void AgentFeatures_AnnounceExam()
        {
            Assert.Equal("X-Agent-Features", AgentFeatures.HeaderName);
            Assert.Contains("exam", AgentFeatures.All);
            Assert.Matches("^[a-z0-9_]+(,[a-z0-9_]+)*$", AgentFeatures.Header);
        }

        // Sunucuya ulaşılamasa da süre dolunca biter
        [Fact]
        public async Task ExamEndsAtUntil_EvenOffline()
        {
            using Worker worker = NewWorker(online: false);
            long until = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
            await worker.HandleExamModeAsync(Json(Command(until)));
            Assert.True(ExamMode.IsActive);

            Assert.False(await worker.ExamTickAsync(DateTimeOffset.FromUnixTimeSeconds(until - 1), null));
            Assert.True(ExamMode.IsActive);
            Assert.True(await worker.ExamTickAsync(DateTimeOffset.FromUnixTimeSeconds(until), null));
            Assert.False(ExamMode.IsActive);
            Assert.Equal("EXAM_OFF", _tray.Last());
        }

        [Fact]
        public void Capabilities_OldFileWithoutExam_KeepsExamOn()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":false,\"vision_enabled\":true}");
            AgentCapabilities.Load();
            Assert.True(AgentCapabilities.ExamEnabled);
            Assert.False(AgentCapabilities.TerminalEnabled);
            var (disabled, _) = AgentCapabilities.ApplyServerRequest(Json("{\"exam_enabled\":false}"));
            Assert.Equal(new[] { AgentCapabilities.Exam }, disabled);
            Assert.False(AgentCapabilities.ExamEnabled);
        }

        [Fact]
        public void AuditEvents()
        {
            var settings = new ExamSettings { Allow = { "sinav.meb.gov.tr" }, Until = 1791200000, BlockApps = { "cmd.exe" } };
            LocalAuditEvent started = LocalAudit.ExamStarted(settings);
            Assert.Equal((1110, LocalAuditLevel.Warning), (started.EventId, started.Level));
            Assert.Contains("allow: sinav.meb.gov.tr", started.Message);
            Assert.Equal(1111, LocalAudit.ExamEnded("until").EventId);
            Assert.Equal(1112, LocalAudit.ExamAppStopped("cmd.exe", 42).EventId);
        }
    }
}
