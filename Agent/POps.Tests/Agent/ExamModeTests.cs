using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Sınav modu: emrin doğrulanması, güvenlik duvarı kuralları (ayrı grup), süresinde bitmesi, uygulama engeli
    public class ExamModeTests : TestBase, IDisposable
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
            string json = JsonSerializer.Serialize(new { allow = Enumerable.Range(1, ExamMode.MaxAllow + 1).Select(i => $"10.0.{i / 250}.{i % 250 + 1}") });
            Assert.False(ExamMode.TryParse(Json(json), Now, out _, out string error));
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
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":true,\"vision_enabled\":true,\"exam_enabled\":false}");
            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.ExamEnabled);
            using Worker worker = NewWorker();
            await worker.HandleExamModeAsync(Json(Command(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600)));
            Assert.Empty(_scripts);
            lock (_sent) Assert.Contains(_sent, m => m.GetProperty("type").GetString() == "capability_denied" && m.GetProperty("capability").GetString() == "exam");
            Assert.False(LastState().GetProperty("enabled").GetBoolean());
            Assert.Equal(false, AgentCapabilities.StatusMessage()[AgentCapabilities.Exam]);
        }

        [Fact]
        public async Task InvalidCommand_IsReportedWithTheReason()
        {
            using Worker worker = NewWorker();
            await worker.HandleExamModeAsync(Json("{\"action\":\"exam_mode\",\"enabled\":true,\"allow\":[\"bad entry\"]}"));
            Assert.Empty(_scripts);
            Assert.Contains("geçersiz", LastState().GetProperty("detail").GetString());
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
