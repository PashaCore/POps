using System;
using System.Collections.Generic;
using System.Globalization;
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
    // Sınav modu: emrin doğrulanması, güvenlik duvarı kuralları (ayrı grup), süresinde bitmesi (sunucunun now'ıyla, monotonik
    // saatle, en çok 8 saat), uygulama engeli
    public class ExamModeTests : TestBase, IDisposable
    {
        private readonly List<string> _scripts = new List<string>();
        private IPAddress[] _resolved = { IPAddress.Parse("198.51.100.7") };
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        // Testin saati: bilgisayarın saati Now'dan başlar; monotonik saat ayrı (bkz. Clock)
        private readonly Clock _clock = new Clock(Now);
        // Testin ajanı: güvenlik duvarı betikleri _scripts'e yazılır, izin listesinin ad çözümü _resolved (makine kapıları)
        private readonly AgentHarness _agent;
        private ExamMode _exam;

        public ExamModeTests()
        {
            _agent = AgentHarness.Create("exam", machine: new MachineSeams
            {
                Kiosk = new FakeKioskRegistry(),
                FirewallScripts = script =>
                {
                    lock (_scripts) _scripts.Add(script);
                    return Task.FromResult((0, "[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Public\",\"Enabled\":\"False\"}]"));
                },
                ExamResolver = _ => Task.FromResult(_resolved),
            }, clock: _clock);
            _agent.Capabilities.Load();
        }

        public void Dispose() => _agent.Dispose();

        // Bilgisayarın saati (GetUtcNow) ve monotonik saat (GetTimestamp) ayrı ayrı. Advance: gerçek zaman geçer, ikisi birlikte
        // ilerler; SetPc: bilgisayarın saati ayarlanır (elle ya da eşitleme), monotonik saat değişmez; Reboot: bilgisayar yeniden
        // başlar, monotonik saat sıfırdan sayar, bilgisayarın saati kayabilir.
        private sealed class Clock : TimeProvider
        {
            private readonly object _gate = new object();
            private DateTimeOffset _pc;
            private long _ticks = 5_000_000_000;

            public Clock(DateTimeOffset pc) => _pc = pc;

            public override DateTimeOffset GetUtcNow() { lock (_gate) return _pc; }
            public override long GetTimestamp() { lock (_gate) return _ticks; }
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public void Advance(TimeSpan by)
            {
                lock (_gate)
                {
                    _pc += by;
                    _ticks += by.Ticks;
                }
            }

            public void SetPc(DateTimeOffset pc) { lock (_gate) _pc = pc; }

            public void Reboot(TimeSpan pcDrift)
            {
                lock (_gate)
                {
                    _pc += pcDrift;
                    _ticks = 1_000;
                }
            }
        }

        // Bilgisayarın saati (unix sn)
        private long PcNow => _clock.GetUtcNow().ToUnixTimeSeconds();

        // X-Agent-Features: bu bağlamın yetenek durumundan (bkz. CommandConnection)
        private AgentFeatures AgentFeatures => new AgentFeatures(_agent.Capabilities);

        // Bu bağlamın sınav modu (Worker'ınkiyle aynı durum dosyası, makine kapıları ve saat)
        private ExamMode ExamMode => _exam ??= new ExamMode(_agent.Paths.SecureFile(ExamMode.StateFileName), _agent.Machine.FirewallScripts,
            _agent.Machine.ExamResolver, _agent.Machine.StopProcess, _agent.Context.Clock);

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        private static string Command(long until) =>
            "{\"action\":\"exam_mode\",\"enabled\":true,\"allow\":[\"Sinav.MEB.gov.tr.\",\"10.0.0.5\",\"10.1.0.9/24\"],\"until\":" + until +
            ",\"message\":\"Sınav modu: yalnızca sınav sitesi açık\\u0007\",\"block_apps\":[\"cmd.exe\",\"Explorer.exe\",\"powershell.exe\"]}";

        // Süre testlerinin emri: until (JSON değeri; null: alan yok) ve sunucunun now'ı (null: yok, eski sunucu). Engelli uygulama
        // yok: tur bu bilgisayarın süreçlerini taramaz.
        private static string Timed(string until, long? now) =>
            "{\"action\":\"exam_mode\",\"enabled\":true,\"allow\":[],\"message\":\"\"" + (until == null ? "" : ",\"until\":" + until) +
            (now == null ? "" : ",\"now\":" + now.Value.ToString(CultureInfo.InvariantCulture)) + ",\"block_apps\":[]}";

        private static string Timed(long until, long? now) => Timed(until.ToString(CultureInfo.InvariantCulture), now);

        [Fact]
        public void Command_IsValidatedAndNormalized()
        {
            Assert.True(ExamMode.TryParse(Json(Command(Now.ToUnixTimeSeconds() + 3600)), Now, out ExamSettings s, out string error), error);
            Assert.Equal(new[] { "sinav.meb.gov.tr", "10.0.0.5", "10.1.0.9/24" }, s.Allow);
            Assert.Equal(Now.ToUnixTimeSeconds() + 3600, s.Until);
            Assert.Equal(new[] { "cmd.exe", "powershell.exe" }, s.BlockApps);   // korunan süreç (explorer) düşer
            Assert.DoesNotContain('\u0007', s.Message);
            Assert.Equal(Now.ToUnixTimeSeconds(), s.Since);
            // now yok (eski sunucu): kalan süre until - bu bilgisayarın saati; bitiş bu bilgisayarın saatiyle
            Assert.Equal(TimeSpan.FromHours(1), s.Remaining);
            Assert.Equal(Now.ToUnixTimeSeconds() + 3600, s.EndsAt);
        }

        // Kalan süre until - now (sunucunun iki saati); bitiş (ends_at) bu bilgisayarın saatiyle, until sunucunun değeri olarak kalır
        [Theory]
        [InlineData(3600)]    // bilgisayarın saati 1 saat ileride
        [InlineData(-3600)]   // 1 saat geride
        [InlineData(0)]
        public void ServerNow_GivesTheTimeLeft_WhateverThePcClockSays(int pcAheadSeconds)
        {
            long serverNow = Now.ToUnixTimeSeconds() - pcAheadSeconds;
            Assert.True(ExamMode.TryParse(Json(Timed(serverNow + 600, serverNow)), Now, out ExamSettings s, out string error), error);
            Assert.Equal(TimeSpan.FromMinutes(10), s.Remaining);
            Assert.Equal(serverNow + 600, s.Until);
            Assert.Equal(Now.ToUnixTimeSeconds() + 600, s.EndsAt);
            Assert.Equal(Now.ToUnixTimeSeconds(), s.Since);
        }

        // now'ın türü yanlışsa yok sayılır (protokolün kuralı): eski sunucu gibi bu bilgisayarın saatiyle
        [Theory]
        [InlineData("\"1791194400\"")]
        [InlineData("1791194400.5")]
        [InlineData("null")]
        public void ServerNow_WithTheWrongType_IsIgnored(string now)
        {
            string json = "{\"until\":" + (Now.ToUnixTimeSeconds() + 600).ToString(CultureInfo.InvariantCulture) + ",\"now\":" + now + "}";
            Assert.True(ExamMode.TryParse(Json(json), Now, out ExamSettings s, out string error), error);
            Assert.Equal(TimeSpan.FromMinutes(10), s.Remaining);
        }

        // En çok 8 saat: until yoksa, null ise ya da daha ilerideyse
        [Theory]
        [InlineData(null)]
        [InlineData("null")]
        [InlineData("1791226800")]   // Now + 10 saat
        public void TimeLeft_IsAtMostEightHours(string until)
        {
            Assert.True(ExamMode.TryParse(Json(Timed(until, Now.ToUnixTimeSeconds())), Now, out ExamSettings s, out string error), error);
            Assert.Equal(ExamMode.MaxDuration, s.Remaining);
            Assert.Equal(TimeSpan.FromHours(8), ExamMode.MaxDuration);
            Assert.Equal(Now.ToUnixTimeSeconds() + 8 * 3600, s.EndsAt);
            Assert.Equal(until == "1791226800" ? 1791226800L : (long?)null, s.Until);   // exam_state.until: sunucunun değeri aynen
        }

        // Servisin saati: monotonik saat Environment.TickCount64 (ms; uyku ve hazırda bekleme süresini de sayar), bilgisayarın
        // saati DateTimeOffset.UtcNow
        [Fact]
        public void ServiceClock_CountsLikeTickCount64()
        {
            long before = Environment.TickCount64;
            long stamp = AgentClock.Instance.GetTimestamp();
            long after = Environment.TickCount64;
            Assert.InRange(stamp, before, after);
            Assert.Equal(1000, AgentClock.Instance.TimestampFrequency);
            Assert.Equal(TimeSpan.FromSeconds(90), AgentClock.Instance.GetElapsedTime(stamp, stamp + 90_000));
            Assert.InRange(AgentClock.Instance.GetUtcNow(), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        }

        [Fact]
        public void TimeLeft_DoesNotOverflow()
        {
            Assert.Equal(ExamMode.MaxDuration, ExamMode.TimeLeft(long.MaxValue, long.MinValue));
            Assert.Equal(TimeSpan.FromSeconds(1), ExamMode.TimeLeft(long.MinValue + 1, long.MinValue));
            Assert.Null(ExamMode.TimeLeft(long.MinValue, long.MaxValue));
            Assert.Null(ExamMode.TimeLeft(5, 5));
        }

        [Theory]
        [InlineData("{\"allow\":[\"http://site.example\"]}", "geçersiz")]
        [InlineData("{\"allow\":[\"*.site.example\"]}", "geçersiz")]
        [InlineData("{\"allow\":[\"a b\"]}", "geçersiz")]
        [InlineData("{\"allow\":[\"10.0.0.0/4\"]}", "geçersiz")]
        [InlineData("{\"until\":1000}", "geçmişte")]
        // until - now sıfır ya da geçmişte: bilgisayarın saatine göre (Now = 1791190800) 10 dakika sonra olsa da
        [InlineData("{\"until\":1791191400,\"now\":1791191400}", "geçmişte (sunucunun saatiyle)")]
        [InlineData("{\"until\":1791191400,\"now\":1791194400}", "geçmişte (sunucunun saatiyle)")]
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

        private Worker NewWorker(bool online = true) => new Worker(NullLogger<Worker>.Instance, _agent.Context)
        {
            HwId = "HW-EXAM",
            SendOverride = p => { lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(p)); return Task.FromResult(online); },
            TrayOverride = m => { lock (_tray) _tray.Add(m); },
        };

        private JsonElement LastState() { lock (_sent) return _sent.Last(m => m.GetProperty("type").GetString() == "exam_state"); }

        // Tepsi bandının son bitişi (EXAM_ON'daki until)
        private long? BannerUntil()
        {
            string banner;
            lock (_tray) banner = _tray.Last(m => m.StartsWith("EXAM_ON:", StringComparison.Ordinal));
            using JsonDocument doc = JsonDocument.Parse(Convert.FromBase64String(banner.Substring("EXAM_ON:".Length)));
            return doc.RootElement.GetProperty("until").ValueKind == JsonValueKind.Number ? doc.RootElement.GetProperty("until").GetInt64() : null;
        }

        [Fact]
        public async Task ExamCommand_AppliesAndReports_ThenEnds()
        {
            using Worker worker = NewWorker();
            long until = PcNow + 3600;
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
            await worker.HandleExamModeAsync(Json(Command(PcNow + 3600)));
            Assert.Empty(_scripts);
            Assert.False(ExamMode.IsActive);
            lock (_sent) Assert.Equal("capability_denied", Assert.Single(_sent).GetProperty("type").GetString());
            // capabilities mesajı sınav yeteneğinin kapalı olduğunu bildirir (exam_enabled, şemada isteğe bağlı)
            Assert.Equal(false, _agent.Capabilities.StatusMessage()[AgentCapabilities.Exam]);
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
  ""now"": 1791205409,
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

        // Vektördeki until (2026-10-05 13:41:29Z) sabittir: emir, örnek exam_state'in since anında gelmiş sayılır (bilgisayarın
        // saati; sunucunun now'ı 2 dakika sonrası: bu bilgisayarın saati 2 dakika geride)
        private static readonly DateTimeOffset VectorNow = DateTimeOffset.FromUnixTimeSeconds(1791205289);

        // Kopyalar docs/protocol/examples'daki dosyaların metniyle aynı (satır sonları dışında: depoda LF, Windows çalışma
        // kopyasında CRLF olabilir; dosyanın son satır sonu dışında). VectorServerInfo burada denetlenmez: server_info.json'a
        // sonradan power, message ve peer_cache eklendi, kopya önceki listedir.
        [Fact]
        public void VectorCopies_AreTheProtocolExamples()
        {
            static string Example(string direction, string file) =>
                File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "docs", "protocol", "examples", direction, file)).Replace("\r\n", "\n").TrimEnd('\n');
            static string Copy(string text) => text.Replace("\r\n", "\n");

            Assert.Equal(Example("server-to-agent", "exam_mode.json"), Copy(VectorExamMode));
            Assert.Equal(Example("server-to-agent", "exam_mode.off.json"), Copy(VectorExamModeOff));
            Assert.Equal(Example("agent-to-server", "exam_state.json"), Copy(VectorExamState));
            Assert.Equal(Example("agent-to-server", "exam_state.off.json"), Copy(VectorExamStateOff));
            Assert.Equal(Example("agent-to-server", "capability_denied.exam.json"), Copy(VectorCapabilityDeniedExam));
            Assert.Equal(Example("server-to-agent", "server_info.0_1_21.json"), Copy(VectorServerInfoOld));
        }

        private static Task Handle(Worker worker, string json) => worker.HandleServerMessageAsync(json, null, CancellationToken.None);

        private List<JsonElement> Sent(string type) { lock (_sent) return _sent.Where(m => m.TryGetProperty("type", out JsonElement t) && t.GetString() == type).ToList(); }

        private void DisableExamCapabilityLocally()
        {
            SecureStore.WriteProtected(_agent.Paths.SecureFile(AgentCapabilities.FileName), "{\"terminal_enabled\":true,\"vision_enabled\":true,\"exam_enabled\":false}");
            _agent.Capabilities.Load();
            Assert.False(_agent.Capabilities.ExamEnabled);
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
            _clock.SetPc(VectorNow);
            await Handle(worker, VectorExamMode);
            Assert.True(ExamMode.IsActive);
            ExamSettings applied = ExamMode.Load();
            Assert.Equal(new[] { "sinav.meb.gov.tr", "10.0.0.5", "10.1.0.0/24" }, applied.Allow);
            Assert.Equal("Sınav modu: yalnızca sınav sitesi açık", applied.Message);
            Assert.Equal(new[] { "cmd.exe", "powershell.exe" }, applied.BlockApps);
            Assert.Equal(1791207689, applied.Until);
            // Kalan süre until - now = 38 dakika; bitiş bu bilgisayarın (2 dakika geri) saatiyle
            Assert.Equal(VectorNow.ToUnixTimeSeconds() + (1791207689 - 1791205409), applied.EndsAt);
            JsonElement state = Assert.Single(Sent("exam_state"));
            AssertExamStateShape(state);
            AssertSameJson(VectorExamState, state);

            // exam_mode.off.json, 2 dakika sonra: sınav kalkar, exam_state enabled:false (since: çıkış anı, until: null)
            _clock.Advance(TimeSpan.FromSeconds(1791205409 - 1791205289));
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
            _clock.SetPc(VectorNow);
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
            _clock.SetPc(VectorNow);
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
            await worker.HandleExamModeAsync(Json(Command(PcNow + 60)));
            int before = Sent("exam_state").Count;
            worker.OnCommandSocketOpened();
            _clock.Advance(TimeSpan.FromSeconds(60));
            Assert.True(await worker.ExamTickAsync(null));
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
            await worker.HandleExamModeAsync(Json(Command(PcNow + 3600)));
            Assert.True(ExamMode.IsActive);
            DisableExamCapabilityLocally();

            Assert.True(await worker.ExamTickAsync(null));
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
            await worker.HandleExamModeAsync(Json(Command(PcNow + 3600)));
            Assert.True(ExamMode.IsActive);

            await Handle(worker, "{\"action\":\"set_capabilities\",\"exam_enabled\":false}");
            Assert.False(_agent.Capabilities.ExamEnabled);
            Assert.False(ExamMode.IsActive);
            JsonElement capabilities = Assert.Single(Sent("capabilities"));
            Assert.False(capabilities.GetProperty("exam_enabled").GetBoolean());
            Assert.False(LastState().GetProperty("enabled").GetBoolean());

            // Ardından gelen exam_mode reddedilir, uygulanmaz
            int scripts = _scripts.Count;
            await worker.HandleExamModeAsync(Json(Command(PcNow + 3600)));
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
            await worker.HandleExamModeAsync(Json(Command(PcNow + 60)));
            Assert.True(ExamMode.IsActive);

            _clock.Advance(TimeSpan.FromSeconds(59));
            Assert.False(await worker.ExamTickAsync(null));
            Assert.True(ExamMode.IsActive);
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await worker.ExamTickAsync(null));
            Assert.False(ExamMode.IsActive);
            Assert.Equal("EXAM_OFF", _tray.Last());
        }

        // ------------------------------------------------------------------ süre: sunucunun now'ı, monotonik saat, en çok 8 saat
        // Süre doluncaya kadar sürer, dolunca biter (turlar monotonik saatle; bilgisayarın saati ne derse desin)
        private async Task AssertEndsAfter(Worker worker, TimeSpan duration)
        {
            _clock.Advance(duration - TimeSpan.FromSeconds(1));
            Assert.False(await worker.ExamTickAsync(null));
            Assert.True(ExamMode.IsActive);
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await worker.ExamTickAsync(null));
            Assert.False(ExamMode.IsActive);
            Assert.Equal("EXAM_OFF", _tray.Last());
        }

        // 10 dakikalık sınav (sunucunun saatiyle until = now + 10 dk), bilgisayarın saati pcAhead kadar ileride
        private async Task AssertTenMinuteExam(TimeSpan pcAhead)
        {
            using Worker worker = NewWorker();
            long serverNow = PcNow - (long)pcAhead.TotalSeconds;
            long until = serverNow + 600;
            await worker.HandleExamModeAsync(Json(Timed(until, serverNow)));
            Assert.True(ExamMode.IsActive);
            JsonElement state = LastState();
            AssertExamStateShape(state);
            Assert.True(state.GetProperty("enabled").GetBoolean());
            Assert.Equal(until, state.GetProperty("until").GetInt64());   // sunucuya kendi until'i: yeniden göndermez
            // Bant ve kayıtlı bitiş bu bilgisayarın saatiyle: öğrencinin gördüğü saatte biter
            Assert.Equal(PcNow + 600, ExamMode.Load().EndsAt);
            Assert.Equal(PcNow + 600, BannerUntil());
            await AssertEndsAfter(worker, TimeSpan.FromMinutes(10));
        }

        // Önceden until bu bilgisayarın saatiyle karşılaştırılıyordu: sınav reddedilirdi (until 50 dakika önce)
        [Fact]
        public Task PcClockOneHourAhead_TheExamStillLastsItsTime() => AssertTenMinuteExam(TimeSpan.FromHours(1));

        // Önceden 70 dakika sürerdi
        [Fact]
        public Task PcClockOneHourBehind_TheExamStillLastsItsTime() => AssertTenMinuteExam(TimeSpan.FromHours(-1));

        // until yok ya da null: en çok 8 saat (önceden sunucu kapatana kadar sürerdi); exam_state.until null kalır
        [Theory]
        [InlineData(null, true)]
        [InlineData("null", true)]
        [InlineData("null", false)]
        public async Task NoUntil_EndsAfterEightHours(string until, bool serverNow)
        {
            using Worker worker = NewWorker();
            await worker.HandleExamModeAsync(Json(Timed(until, serverNow ? PcNow - 3600 : null)));
            Assert.True(ExamMode.IsActive);
            Assert.Equal(JsonValueKind.Null, LastState().GetProperty("until").ValueKind);
            Assert.Equal(PcNow + ExamMode.MaxSeconds, ExamMode.Load().EndsAt);
            Assert.Equal(PcNow + ExamMode.MaxSeconds, BannerUntil());
            await AssertEndsAfter(worker, ExamMode.MaxDuration);
        }

        // until 10 saat sonra: 8 saatte biter; sunucuya yine kendi until'i bildirilir
        [Theory]
        [InlineData(true)]
        [InlineData(false)]   // eski sunucu (now yok)
        public async Task UntilTenHoursAhead_EndsAfterEightHours(bool serverNow)
        {
            using Worker worker = NewWorker();
            long until = PcNow + 10 * 3600;
            await worker.HandleExamModeAsync(Json(Timed(until, serverNow ? PcNow : null)));
            Assert.True(ExamMode.IsActive);
            Assert.Equal(until, LastState().GetProperty("until").GetInt64());
            Assert.Equal(PcNow + ExamMode.MaxSeconds, ExamMode.Load().EndsAt);
            await AssertEndsAfter(worker, ExamMode.MaxDuration);
        }

        // until - now sıfır ya da geçmişte: bilgisayarın saati 1 saat geride (until ona göre gelecekte) olsa da reddedilir; diğer
        // retler gibi: neden yerel loga, sunucuya o anki durum (exam_state), hiçbir şey uygulanmaz
        [Theory]
        [InlineData(0)]
        [InlineData(-60)]
        public async Task UntilNotAfterTheServersNow_IsRefused(int left)
        {
            using Worker worker = NewWorker();
            long serverNow = PcNow + 3600;
            await worker.HandleExamModeAsync(Json(Timed(serverNow + left, serverNow)));
            Assert.Empty(_scripts);
            Assert.False(ExamMode.IsActive);
            Assert.Empty(_tray);
            JsonElement state = Assert.Single(Sent("exam_state"));
            AssertExamStateShape(state);
            Assert.False(state.GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, state.GetProperty("until").ValueKind);
        }

        // Sınav sürerken bilgisayarın saati 1 saat ileri alındı (saat eşitlendi): bitiş değişmez; kayıtlı bitiş ve bant yeni saate
        // göre yazılır, ardından yeniden başlatma da aynı bitişi bulur
        [Fact]
        public async Task PcClockChangedDuringTheExam_TheEndStays_AlsoAfterARestart()
        {
            using (Worker worker = NewWorker())
            {
                long serverNow = PcNow + 3600;   // bilgisayarın saati 1 saat geride
                await worker.HandleExamModeAsync(Json(Timed(serverNow + 600, serverNow)));
                _clock.Advance(TimeSpan.FromSeconds(120));
                Assert.False(await worker.ExamTickAsync(null));
                int banners = _tray.Count;

                _clock.SetPc(_clock.GetUtcNow().AddHours(1));
                Assert.False(await worker.ExamTickAsync(null));
                Assert.Equal(PcNow + 480, ExamMode.Load().EndsAt);
                Assert.Equal(banners + 1, _tray.Count);
                Assert.Equal(PcNow + 480, BannerUntil());
                _clock.Advance(TimeSpan.FromSeconds(180));
                Assert.False(await worker.ExamTickAsync(null));
                Assert.Equal(banners + 1, _tray.Count);   // kaymadıkça yeniden yazılmaz, bant yeniden gönderilmez
            }

            _clock.Reboot(TimeSpan.FromSeconds(30));   // kapalıyken 30 sn geçti
            using Worker after = NewWorker();
            await AssertEndsAfter(after, TimeSpan.FromSeconds(600 - 120 - 180 - 30));
        }

        // Yeniden başlatma: monotonik saat sıfırdan başlar; kayıtlı bitiş (bilgisayarın saatiyle) kullanılır, sonra yine monotonik
        // saatle sayılır (bilgisayarın saati sonra ayarlansa da)
        [Fact]
        public async Task AfterARestart_TheSavedEndIsUsed()
        {
            using (Worker worker = NewWorker(online: false))
            {
                long serverNow = PcNow - 3600;   // bilgisayarın saati 1 saat ileride
                await worker.HandleExamModeAsync(Json(Timed(serverNow + 600, serverNow)));
                _clock.Advance(TimeSpan.FromSeconds(240));
                Assert.False(await worker.ExamTickAsync(null));
            }

            _clock.Reboot(TimeSpan.FromSeconds(60));
            using Worker after = NewWorker(online: false);
            Assert.False(await after.ExamTickAsync(null));   // bitiş kayıttan kurulur: 5 dakika kaldı
            Assert.True(ExamMode.IsActive);
            _clock.SetPc(_clock.GetUtcNow().AddHours(-3));
            await AssertEndsAfter(after, TimeSpan.FromSeconds(300));
        }

        // Yeniden başlatmada bilgisayarın saati bir gün geri gitti (ör. BIOS pili bitti): kayıtlı bitiş en çok 8 saat sonrasıdır;
        // kısaltılan bitiş yazılır ve bantta gösterilir
        [Fact]
        public async Task AfterARestart_TheSavedEndIsAtMostEightHoursAhead()
        {
            using (Worker worker = NewWorker(online: false))
                await worker.HandleExamModeAsync(Json(Timed(PcNow + 3600, PcNow)));

            _clock.Reboot(TimeSpan.FromDays(-1));
            using Worker after = NewWorker(online: false);
            Assert.False(await after.ExamTickAsync(null));
            Assert.Equal(PcNow + ExamMode.MaxSeconds, ExamMode.Load().EndsAt);
            Assert.Equal(PcNow + ExamMode.MaxSeconds, BannerUntil());
            await AssertEndsAfter(after, ExamMode.MaxDuration);
        }

        // Kayıtlı bitiş geçmişte (bilgisayarın saati ileri gitti): ilk turda biter
        [Fact]
        public async Task AfterARestart_ASavedEndInThePast_EndsTheExam()
        {
            using (Worker worker = NewWorker(online: false))
                await worker.HandleExamModeAsync(Json(Timed(PcNow + 600, PcNow)));

            _clock.Reboot(TimeSpan.FromSeconds(600));
            using Worker after = NewWorker(online: false);
            Assert.True(await after.ExamTickAsync(null));
            Assert.False(ExamMode.IsActive);
        }

        // Bağlanınca sunucunun gönderdiği durum geçerlidir: yeniden başlatmadan sonra kısaltılan bitişin yerine sunucunun kalan
        // süresi geçer
        [Fact]
        public async Task AfterARestart_TheServersExamModeWins()
        {
            using (Worker worker = NewWorker())
                await worker.HandleExamModeAsync(Json(Timed(PcNow + 3600, PcNow)));

            _clock.Reboot(TimeSpan.FromDays(-1));
            using Worker after = NewWorker();
            Assert.False(await after.ExamTickAsync(null));
            Assert.Equal(PcNow + ExamMode.MaxSeconds, ExamMode.Load().EndsAt);

            long serverNow = PcNow + 86400;   // sunucunun saati doğru, bilgisayarınki bir gün geride
            await after.HandleExamModeAsync(Json(Timed(serverNow + 1200, serverNow)));
            Assert.Equal(serverNow + 1200, LastState().GetProperty("until").GetInt64());
            Assert.Equal(PcNow + 1200, ExamMode.Load().EndsAt);
            await AssertEndsAfter(after, TimeSpan.FromMinutes(20));
        }

        // 0.1.24'ten önceki sürümün durum dosyası (ends_at yok): bitiş sunucunun until'i, o da yoksa girişten 8 saat sonra
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task OldStateFile_WithoutEndsAt_IsResumed(bool withUntil)
        {
            long since = PcNow - 600;
            string until = withUntil ? (PcNow + 900).ToString(CultureInfo.InvariantCulture) : "null";
            SecureStore.WriteProtected(ExamMode.StatePath, "{\"allow\":[],\"until\":" + until + ",\"message\":\"\",\"block_apps\":[],\"since\":" +
                since.ToString(CultureInfo.InvariantCulture) + ",\"addresses\":[],\"previous_disabled_profiles\":[]}");
            using Worker worker = NewWorker(online: false);
            Assert.False(await worker.ExamTickAsync(null));
            TimeSpan left = withUntil ? TimeSpan.FromSeconds(900) : ExamMode.MaxDuration - TimeSpan.FromSeconds(600);
            Assert.Equal(PcNow + (long)left.TotalSeconds, ExamMode.Load().EndsAt);   // bundan sonra ends_at ile sürer
            await AssertEndsAfter(worker, left);
        }

        [Fact]
        public void Capabilities_OldFileWithoutExam_KeepsExamOn()
        {
            SecureStore.WriteProtected(_agent.Paths.SecureFile(AgentCapabilities.FileName), "{\"terminal_enabled\":false,\"vision_enabled\":true}");
            _agent.Capabilities.Load();
            Assert.True(_agent.Capabilities.ExamEnabled);
            Assert.False(_agent.Capabilities.TerminalEnabled);
            var (disabled, _) = _agent.Capabilities.ApplyServerRequest(Json("{\"exam_enabled\":false}"));
            Assert.Equal(new[] { AgentCapabilities.Exam }, disabled);
            Assert.False(_agent.Capabilities.ExamEnabled);
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
