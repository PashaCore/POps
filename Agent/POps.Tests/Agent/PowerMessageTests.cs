using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Uzaktan güç işlemleri (power) ve kullanıcı mesajları (user_message). Makineye dokunan her şey sahtedir: güç API'si
    // (PowerActions.Execute) yalnızca çağrıyı kaydeder, oturum sorgusu (SessionTasks.HasConsoleUser) testin değeridir,
    // bekleme (SessionTasks.Delay) anında ya da test bırakınca biter, tepsi borusu TrayOverride'dır. Bu testler bilgisayarı
    // asla kapatmaz, yeniden başlatmaz, oturumu kapatmaz ya da kilitlemez.
    [Collection(SharedStateCollection.Name)]
    public class PowerMessageTests : SharedStateTestBase, IDisposable
    {
        private readonly Worker _worker;
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private readonly List<string> _tray = new List<string>();
        private readonly List<LocalAuditEvent> _audit = new List<LocalAuditEvent>();
        private readonly List<(PowerOperation Operation, int TaskId)> _powerCalls = new List<(PowerOperation, int)>();
        // Sonuç ve güç çağrısı sırası ("result:<task_id>:<exit_code>", "power:<op>")
        private readonly List<string> _timeline = new List<string>();
        private readonly List<TimeSpan> _delays = new List<TimeSpan>();
        private readonly TaskCompletionSource _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<string> _onTray;
        private bool _consoleUser = true;
        private bool _trayConnected = true;
        private bool _trayInConsole;

        public PowerMessageTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("power-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("power-data");
            AgentCapabilities.Load();   // dosya yok: hepsi açık
            _worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-TEST",
                SendOverride = payload =>
                {
                    JsonElement message = JsonSerializer.SerializeToElement(payload);
                    lock (_timeline)
                    {
                        _sent.Add(message);
                        if (message.TryGetProperty("type", out JsonElement type) && type.GetString() == "result")
                            _timeline.Add($"result:{message.GetProperty("task_id").GetInt32()}:{message.GetProperty("exit_code").GetInt32()}");
                    }
                    return Task.FromResult(true);
                },
                TrayOverride = message =>
                {
                    lock (_timeline) _tray.Add(message);
                    _onTray?.Invoke(message);
                },
                TrayConnectedOverride = () => _trayConnected,
                TrayInConsoleOverride = () => _trayInConsole,
            };
            _worker.Power.Audit = Record;
            _worker.Messages.Audit = Record;
            PowerActions.Execute = (operation, taskId) =>
            {
                lock (_timeline)
                {
                    _powerCalls.Add((operation, taskId));
                    _timeline.Add("power:" + operation);
                }
                return true;
            };
            SessionTasks.HasConsoleUser = () => _consoleUser;
            UseInstantDelay();
        }

        // Bırakılmamış beklemeler (_release) sonsuza dek bekler: süren bir geri sayım bir sonraki testin sahtesini çağıramaz
        public void Dispose()
        {
            _worker.Dispose();
            SecureStore.Dir = TestEnvironment.NewDir("power-reset");
            AgentCapabilities.Load();
            TestEnvironment.IsolatePowerAndSessions();
        }

        private void Record(LocalAuditEvent item)
        {
            lock (_timeline) _audit.Add(item);
        }

        private void UseInstantDelay() => SessionTasks.Delay = (time, token) =>
        {
            lock (_delays) _delays.Add(time);
            return Task.CompletedTask;
        };

        // Bekleme test _release'i tamamlayana (ya da iptal edilene) kadar sürer
        private void UseManualDelay() => SessionTasks.Delay = (time, token) =>
        {
            lock (_delays) _delays.Add(time);
            return _release.Task.WaitAsync(token);
        };

        private Task Handle(string json) => _worker.HandleServerMessageAsync(json, null, CancellationToken.None);

        private List<JsonElement> Sent(string type)
        {
            lock (_timeline) return _sent.Where(m => m.TryGetProperty("type", out JsonElement t) && t.GetString() == type).ToList();
        }

        private List<JsonElement> Results(int taskId) => Sent("result").Where(m => m.GetProperty("task_id").GetInt32() == taskId).ToList();

        private List<string> Tray(string prefix)
        {
            lock (_timeline) return _tray.Where(m => m.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        private List<LocalAuditEvent> Audits(int eventId)
        {
            lock (_timeline) return _audit.Where(a => a.EventId == eventId).ToList();
        }

        private List<(PowerOperation Operation, int TaskId)> PowerCalls()
        {
            lock (_timeline) return _powerCalls.ToList();
        }

        // Arka plandaki görevin beklemeye girdiği süreler
        private List<TimeSpan> Delays()
        {
            lock (_delays) return _delays.ToList();
        }

        private static async Task WaitFor(Func<bool> condition, string what)
        {
            for (int i = 0; i < 250; i++)
            {
                if (condition()) return;
                await Task.Delay(20);
            }
            throw new TimeoutException(what);
        }

        private async Task<JsonElement> WaitForResult(int taskId)
        {
            await WaitFor(() => Results(taskId).Count > 0, $"Görev {taskId} sonucu gelmedi");
            return Results(taskId).Single();
        }

        // Güç işlemi bitti (geri sayım ya da uygulama sürmüyor)
        private Task WaitUntilPowerIdle() => WaitFor(() => _worker.Power.CurrentTaskId == null, "Güç işlemi bitmedi");

        private static JsonElement Decode(string trayMessage, string prefix) =>
            JsonDocument.Parse(Convert.FromBase64String(trayMessage.Substring(prefix.Length))).RootElement.Clone();

        private static void WriteCapabilities(string json)
        {
            File.WriteAllText(SecureStore.PathOf(AgentCapabilities.FileName), json);
            AgentCapabilities.Load();
        }

        private static string Power(int taskId, string op, int delay = 0, string extra = "") =>
            $"{{\"action\":\"power\",\"task_id\":{taskId},\"op\":\"{op}\",\"delay\":{delay}{extra},\"requested_by\":\"Pasha\"}}";

        private static string Message(int taskId, string text, bool requiresAck, string extra = "") =>
            $"{{\"action\":\"user_message\",\"task_id\":{taskId},\"title\":\"Duyuru\",\"text\":{JsonSerializer.Serialize(text)},\"style\":\"info\",\"requires_ack\":{(requiresAck ? "true" : "false")}{extra},\"requested_by\":\"Pasha\"}}";

        // Sunucunun gönderdiği biçimde geçerli bir mesaj; field verilirse o alan value ile değiştirilir (value null: alan yok)
        private static string MessageWith(int taskId, string field, string value)
        {
            var fields = new List<(string Name, string Json)>
            {
                ("action", "\"user_message\""), ("task_id", taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("title", "\"Duyuru\""), ("text", "\"Merhaba\""), ("style", "\"info\""), ("requires_ack", "false"), ("requested_by", "\"Pasha\""),
            };
            int index = fields.FindIndex(f => f.Name == field);
            if (index >= 0)
            {
                if (value == null) fields.RemoveAt(index);
                else fields[index] = (field, value);
            }
            return "{" + string.Join(",", fields.Select(f => $"\"{f.Name}\":{f.Json}")) + "}";
        }

        // ------------------------------------------------------------------ özellik duyurusu

        [Fact]
        public void FeaturesHeader_AnnouncesPowerAndMessage()
        {
            Assert.Equal("X-Agent-Features", AgentFeatures.HeaderName);
            Assert.Contains("power", AgentFeatures.Header.Split(','));
            Assert.Contains("message", AgentFeatures.Header.Split(','));
            Assert.Matches(new Regex("^[a-z0-9_]+(,[a-z0-9_]+)*$"), AgentFeatures.Header);

            // Komut tüneli başlığı sürüm başlığının hemen ardından ekler
            string worker = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "Agent", "POps.Agent", "POps.Agent", "Worker.cs"));
            Assert.Contains("_commandWs.Options.SetRequestHeader(\"X-Agent-Version\", AppVersion);\n                _commandWs.Options.SetRequestHeader(AgentFeatures.HeaderName, AgentFeatures.Header);",
                worker.Replace("\r\n", "\n"));
        }

        [Fact]
        public void NoUserCode_IsMinusSix_AndDuplicateKeepsItsMeaning()
        {
            // -6 iki anlamlı: CommandRunner'da "yinelenen" (hiç gönderilmez), güç/mesajda "oturum açık kullanıcı yok"
            Assert.Equal(-6, SessionTasks.ExitNoUser);
            Assert.Equal(-6, CommandRunner.ExitDuplicate);
            Assert.Equal("[REDDEDİLDİ] oturum açık kullanıcı yok", SessionTasks.NoUserOutput);
        }

        // ------------------------------------------------------------------ doğrulama

        [Theory]
        [InlineData("{\"action\":\"power\",\"task_id\":7}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"delay\":0}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"hibernate\",\"delay\":0}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"Shutdown\",\"delay\":0}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":1,\"delay\":0}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":null,\"delay\":0}")]
        // delay zorunlu (şema): yok ya da null da geçersiz
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\"}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":null}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":601}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":-1}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":1.5}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":\"60\"}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":true}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":0,\"message\":42}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":0,\"message\":[\"a\"]}")]
        [InlineData("{\"action\":\"power\",\"task_id\":7,\"op\":\"restart\",\"delay\":0,\"message\":\"\\ud800\"}")]
        public async Task Power_InvalidFields_AreRefusedWithoutCapabilityDenied(string json)
        {
            await Handle(json);
            JsonElement result = Assert.Single(Results(7));
            Assert.Equal(CommandRunner.ExitDenied, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith("[REDDEDİLDİ] Geçersiz güç isteği", result.GetProperty("output").GetString());
            Assert.Empty(Sent("capability_denied"));
            Assert.Empty(_tray);
            Assert.Empty(_audit);
            Assert.Empty(PowerCalls());
        }

        [Fact]
        public async Task Power_MessageLongerThan200Characters_IsRefused()
        {
            await Handle(Power(8, "restart", 30, $",\"message\":\"{new string('a', 201)}\""));
            Assert.Equal(CommandRunner.ExitDenied, Assert.Single(Results(8)).GetProperty("exit_code").GetInt32());

            // 200 karakter (Unicode karakteri; emoji iki UTF-16 birimi olsa da bir karakter) kabul edilir
            await Handle(Power(9, "shutdown", 0, $",\"message\":\"{string.Concat(Enumerable.Repeat("😀", 200))}\""));
            Assert.Equal(0, (await WaitForResult(9)).GetProperty("exit_code").GetInt32());
        }

        [Theory]
        [InlineData("{\"action\":\"power\",\"op\":\"shutdown\"}")]
        [InlineData("{\"action\":\"power\",\"task_id\":\"7\",\"op\":\"shutdown\"}")]
        [InlineData("{\"action\":\"power\",\"task_id\":0,\"op\":\"shutdown\"}")]
        [InlineData("{\"action\":\"user_message\",\"task_id\":7.5,\"text\":\"x\"}")]
        public async Task WithoutAnIntegerTaskId_NothingIsDoneOrSent(string json)
        {
            await Handle(json);
            Assert.Empty(_sent);
            Assert.Empty(_tray);
            Assert.Empty(PowerCalls());
        }

        [Theory]
        // Zorunlu alanlar (şema): title, text, style, requires_ack; yok ya da null geçersiz
        [InlineData("title", null)]
        [InlineData("title", "null")]
        [InlineData("text", null)]
        [InlineData("text", "null")]
        [InlineData("style", null)]
        [InlineData("style", "null")]
        [InlineData("requires_ack", null)]
        [InlineData("requires_ack", "null")]
        // Tür, boyut ve değer
        [InlineData("title", "\"\"")]
        [InlineData("title", "5")]
        [InlineData("title", "\"\\u200B\\u202E \\u0007\"")]
        [InlineData("text", "\"\"")]
        [InlineData("text", "\"   \"")]
        [InlineData("text", "\"\\u0007\\u0001\\uFEFF\"")]
        [InlineData("text", "\"\\n\\n\"")]
        [InlineData("text", "5")]
        [InlineData("text", "\"\\ud800\"")]
        [InlineData("style", "\"error\"")]
        [InlineData("style", "\"INFO\"")]
        [InlineData("requires_ack", "\"yes\"")]
        [InlineData("requires_ack", "1")]
        public async Task UserMessage_InvalidFields_AreRefusedWithoutCapabilityDenied(string field, string value)
        {
            await Handle(MessageWith(7, field, value));
            JsonElement result = Assert.Single(Results(7));
            Assert.Equal(CommandRunner.ExitDenied, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith("[REDDEDİLDİ] Geçersiz mesaj", result.GetProperty("output").GetString());
            Assert.Empty(Sent("capability_denied"));
            Assert.Empty(_tray);
            Assert.Empty(_audit);
        }

        [Fact]
        public async Task UserMessage_TitleAndTextLimits_CountUnicodeCharacters()
        {
            await Handle(MessageWith(11, "title", $"\"{new string('b', 81)}\""));
            await Handle(MessageWith(12, "text", $"\"{new string('c', 1001)}\""));
            Assert.Equal(CommandRunner.ExitDenied, Assert.Single(Results(11)).GetProperty("exit_code").GetInt32());
            Assert.Equal(CommandRunner.ExitDenied, Assert.Single(Results(12)).GetProperty("exit_code").GetInt32());

            string title = string.Concat(Enumerable.Repeat("ğ😀", 40));   // 80 karakter, 120 UTF-16 birimi
            await Handle(MessageWith(13, "title", $"\"{title}\"").Replace("\"Merhaba\"", $"\"{new string('ş', 1000)}\"", StringComparison.Ordinal));
            Assert.Equal(UserMessages.ShownOutput, Assert.Single(Results(13)).GetProperty("output").GetString());
            JsonElement shown = Decode(Assert.Single(Tray(UserMessages.ShowPrefix)), UserMessages.ShowPrefix);
            Assert.Equal(title, shown.GetProperty("title").GetString());
            Assert.Equal(1000, shown.GetProperty("text").GetString().Length);
        }

        [Fact]
        public void TextCleaning_FollowsTheServerRules()
        {
            // Metin: \r\n ve \r -> \n, sekme -> boşluk, kontrol karakterleri atılır, satır sonundaki boşluk silinir,
            // art arda en çok bir boş satır
            Assert.Equal("Satır 1\nSatır 2 sekme\nSatır 3", SessionTasks.Clean("Satır 1\r\nSatır 2\tsekme\u0007\rSatır 3", keepLineBreaks: true));
            Assert.Equal("a\n\nb\n\nc", SessionTasks.Clean("a   \n\n\n\n  \nb\n\nc", keepLineBreaks: true));
            Assert.Equal("a\nb", SessionTasks.Clean("\n\n a\u2028b \n\n", keepLineBreaks: true));
            // Tek satır (başlık, güç notu): her satır sonu boşluk olur
            Assert.Equal("Başlık iki satır", SessionTasks.Clean("Başlık\r\niki \u202Esatır\u0000", keepLineBreaks: false));
            Assert.Equal("a b c d", SessionTasks.Clean("a\nb\rc\u2029d", keepLineBreaks: false));
            // Yön, sıfır genişlik ve görünmez biçim karakterleri, BOM, C1 kontrol karakterleri
            Assert.Equal("abcdefghij", SessionTasks.Clean("\u2066a\u2069b\u202Ac\u200Ed\u200Fe\u200Bf\u200Cg\u200Dh\u2060i\uFEFF\u0085j\u001B\u2063", keepLineBreaks: true));
            // Görünen karakterlere dokunulmaz (Türkçe, emoji, birleşik karakter)
            Assert.Equal("Çğİöşü 😀 e\u0301", SessionTasks.Clean("Çğİöşü 😀 e\u0301", keepLineBreaks: false));
            Assert.Equal(2, SessionTasks.Length("ğ😀"));
        }

        [Fact]
        public async Task Power_NoteIsOneLine_AndNullMeansNoNote()
        {
            UseManualDelay();
            await Handle(Power(14, "restart", 60, ",\"message\":\"Ders\\nbitti\\u200B\\u202E kaydedin\\r\\n\""));
            Assert.Equal("Ders bitti kaydedin", Decode(Assert.Single(Tray(PowerActions.CountdownPrefix)), PowerActions.CountdownPrefix).GetProperty("message").GetString());
            await Handle("{\"action\":\"cancel_task\",\"task_id\":14}");
            await WaitUntilPowerIdle();
            lock (_timeline) _tray.Clear();

            await Handle(Power(15, "restart", 60, ",\"message\":null"));
            Assert.Equal("", Decode(Assert.Single(Tray(PowerActions.CountdownPrefix)), PowerActions.CountdownPrefix).GetProperty("message").GetString());
            Assert.Empty(Results(15));
        }

        [Fact]
        public async Task UserMessage_TextKeepsLineBreaks_TitleIsOneLine()
        {
            await Handle(MessageWith(16, "title", "\"Sınav\\nbaşlıyor\\u202E\"").Replace("\"Merhaba\"", "\"Kaydedin.\\r\\n\\r\\n\\r\\nSınav 10 dakika sonra.\\u200B\"", StringComparison.Ordinal));
            JsonElement shown = Decode(Assert.Single(Tray(UserMessages.ShowPrefix)), UserMessages.ShowPrefix);
            Assert.Equal("Sınav başlıyor", shown.GetProperty("title").GetString());
            Assert.Equal("Kaydedin.\n\nSınav 10 dakika sonra.", shown.GetProperty("text").GetString());
        }

        // ------------------------------------------------------------------ yetenek kapalı

        [Fact]
        public async Task Power_CapabilityOff_IsRefusedAndDenied()
        {
            WriteCapabilities("{\"terminal_enabled\":true,\"vision_enabled\":true,\"power_enabled\":false,\"message_enabled\":true}");
            await Handle(Power(21, "restart", 30));
            JsonElement result = Assert.Single(Results(21));
            Assert.Equal(CommandRunner.ExitDenied, result.GetProperty("exit_code").GetInt32());
            Assert.Equal(PowerActions.DisabledMessage, result.GetProperty("output").GetString());
            JsonElement denied = Assert.Single(Sent("capability_denied"));
            Assert.Equal(("power", "power", 21), (denied.GetProperty("capability").GetString(), denied.GetProperty("action").GetString(), denied.GetProperty("task_id").GetInt32()));
            Assert.Empty(_tray);
            Assert.Empty(PowerCalls());
        }

        [Fact]
        public async Task UserMessage_CapabilityOff_IsRefusedAndDenied()
        {
            WriteCapabilities("{\"terminal_enabled\":true,\"vision_enabled\":true,\"power_enabled\":true,\"message_enabled\":false}");
            await Handle(Message(22, "Merhaba", false));
            JsonElement result = Assert.Single(Results(22));
            Assert.Equal(CommandRunner.ExitDenied, result.GetProperty("exit_code").GetInt32());
            Assert.Equal(UserMessages.DisabledMessage, result.GetProperty("output").GetString());
            JsonElement denied = Assert.Single(Sent("capability_denied"));
            Assert.Equal(("message", "user_message", 22), (denied.GetProperty("capability").GetString(), denied.GetProperty("action").GetString(), denied.GetProperty("task_id").GetInt32()));
            Assert.Empty(_tray);
        }

        [Fact]
        public async Task SetCapabilities_SwitchesPowerAndMessageOff_AndReportsIt()
        {
            await Handle("{\"action\":\"set_capabilities\",\"power_enabled\":false,\"message_enabled\":false}");
            Assert.False(AgentCapabilities.PowerEnabled);
            Assert.False(AgentCapabilities.MessageEnabled);
            Assert.True(AgentCapabilities.TerminalEnabled);
            // capabilities mesajı bugünkü ajanın örneğiyle (capabilities.files.json) aynı alanları taşır
            JsonElement status = Sent("capabilities").Last();
            JsonElement example = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "docs", "protocol", "examples", "agent-to-server", "capabilities.files.json"))).RootElement;
            Assert.Equal(example.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal), status.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.False(status.GetProperty("power_enabled").GetBoolean());
            Assert.False(status.GetProperty("message_enabled").GetBoolean());
            Assert.True(status.GetProperty("exam_enabled").GetBoolean());
            Assert.True(status.GetProperty("terminal_enabled").GetBoolean());

            // Sunucu yeniden açamaz; kapatma diske yazıldı
            await Handle("{\"action\":\"set_capabilities\",\"power_enabled\":true}");
            Assert.False(AgentCapabilities.PowerEnabled);
            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.PowerEnabled);
            Assert.False(AgentCapabilities.MessageEnabled);
        }

        [Fact]
        public async Task SetCapabilities_PowerOff_StopsTheRunningCountdown()
        {
            UseManualDelay();
            await Handle(Power(24, "shutdown", 600));
            await Handle("{\"action\":\"set_capabilities\",\"power_enabled\":false}");
            JsonElement result = await WaitForResult(24);
            Assert.Equal(CommandRunner.ExitCancelled, result.GetProperty("exit_code").GetInt32());
            Assert.Contains("kapatıldığı için", result.GetProperty("output").GetString());
            await WaitUntilPowerIdle();
            Assert.Contains(PowerActions.CancelPrefix + "24", _tray);
            Assert.Empty(PowerCalls());
        }

        [Fact]
        public void CapabilitiesFile_FromBeforePowerAndMessage_KeepsThemOn()
        {
            WriteCapabilities("{\"terminal_enabled\":false,\"vision_enabled\":true,\"source\":\"msi\"}");
            Assert.True(AgentCapabilities.PowerEnabled);
            Assert.True(AgentCapabilities.MessageEnabled);
            Assert.False(AgentCapabilities.TerminalEnabled);

            WriteCapabilities("{ bozuk");
            Assert.False(AgentCapabilities.PowerEnabled);
            Assert.False(AgentCapabilities.MessageEnabled);
        }

        // ------------------------------------------------------------------ oturum açık kullanıcı yok

        [Theory]
        [InlineData("logoff")]
        [InlineData("lock")]
        public async Task Power_LogoffOrLock_WithoutUser_IsMinusSix(string op)
        {
            _consoleUser = false;
            await Handle(Power(31, op, 60));
            JsonElement result = Assert.Single(Results(31));
            Assert.Equal(SessionTasks.ExitNoUser, result.GetProperty("exit_code").GetInt32());
            Assert.Equal(SessionTasks.NoUserOutput, result.GetProperty("output").GetString());
            Assert.Empty(Sent("capability_denied"));
            Assert.Empty(_tray);
            Assert.Empty(_audit);
            Assert.Empty(PowerCalls());
        }

        [Fact]
        public async Task Power_ShutdownWithoutUser_IsStillDone()
        {
            _consoleUser = false;
            _trayConnected = false;
            await Handle(Power(32, "shutdown"));
            await WaitUntilPowerIdle();
            Assert.Equal((PowerOperation.Shutdown, 32), Assert.Single(PowerCalls()));
            Assert.Equal("[TAMAM] Bilgisayar kapatılıyor.", Assert.Single(Results(32)).GetProperty("output").GetString());
        }

        [Fact]
        public async Task UserMessage_WithoutUser_IsMinusSix()
        {
            _consoleUser = false;
            _trayConnected = false;
            await Handle(Message(33, "Merhaba", true));
            JsonElement result = Assert.Single(Results(33));
            Assert.Equal(SessionTasks.ExitNoUser, result.GetProperty("exit_code").GetInt32());
            Assert.Equal(SessionTasks.NoUserOutput, result.GetProperty("output").GetString());
            Assert.Empty(_tray);
            Assert.Empty(_audit);
        }

        // -6 sunucuda yalnızca "kimse oturum açmamış" demektir: kullanıcı varken tepsi yoksa ajan hatası (-3, Failed)
        [Fact]
        public async Task UserMessage_UserButNoTray_IsAnAgentErrorNotMinusSix()
        {
            _trayConnected = false;
            await Handle(Message(34, "Merhaba", false));
            JsonElement result = Assert.Single(Results(34));
            Assert.Equal(CommandRunner.ExitAgentError, result.GetProperty("exit_code").GetInt32());
            Assert.Equal(UserMessages.NoTrayOutput, result.GetProperty("output").GetString());
            Assert.StartsWith("[HATA]", result.GetProperty("output").GetString());
            Assert.Empty(_tray);
            Assert.Empty(_audit);
        }

        // ------------------------------------------------------------------ güç: geri sayım

        [Fact]
        public async Task Power_Countdown_TrayShowsIt_ResultIsSentBeforeTheAction()
        {
            UseManualDelay();
            await Handle(Power(41, "restart", 60, ",\"message\":\"Ders bitti,\\u0007 kaydedin\""));

            JsonElement countdown = Decode(Assert.Single(Tray(PowerActions.CountdownPrefix)), PowerActions.CountdownPrefix);
            Assert.Equal(41, countdown.GetProperty("task_id").GetInt32());
            Assert.Equal("restart", countdown.GetProperty("op").GetString());
            Assert.Equal(60, countdown.GetProperty("seconds").GetInt32());
            Assert.Equal("Ders bitti, kaydedin", countdown.GetProperty("message").GetString());
            await WaitFor(() => Delays().Count == 1, "Geri sayım başlamadı");
            Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(Delays()));
            // Süre dolmadan hiçbir şey yapılmaz ve sonuç gitmez
            Assert.Empty(Results(41));
            Assert.Empty(PowerCalls());

            _release.SetResult();
            await WaitUntilPowerIdle();
            Assert.Equal((PowerOperation.Restart, 41), Assert.Single(PowerCalls()));
            JsonElement result = Assert.Single(Results(41));
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal("[TAMAM] Bilgisayar yeniden başlatılıyor.", result.GetProperty("output").GetString());
            Assert.Equal("HW-TEST", result.GetProperty("pc_name").GetString());
            lock (_timeline) Assert.Equal(new[] { "result:41:0", "power:Restart" }, _timeline);

            LocalAuditEvent accepted = Assert.Single(Audits(1130));
            Assert.Contains("op: restart", accepted.Message);
            Assert.Contains("delay: 60", accepted.Message);
            Assert.Contains("requested_by: Pasha", accepted.Message);
            Assert.Contains("task_id: 41", accepted.Message);
            Assert.DoesNotContain("Ders bitti", accepted.Message);
        }

        [Fact]
        public async Task Power_WithoutDelay_ActsAtOnce_WithoutCountdown()
        {
            await Handle("{\"action\":\"power\",\"task_id\":42,\"op\":\"logoff\",\"delay\":0,\"message\":null}");
            await WaitUntilPowerIdle();
            Assert.Empty(Tray(PowerActions.CountdownPrefix));
            Assert.Empty(Delays());
            Assert.Equal((PowerOperation.Logoff, 42), Assert.Single(PowerCalls()));
            Assert.Equal("[TAMAM] Kullanıcının oturumu kapatılıyor.", Assert.Single(Results(42)).GetProperty("output").GetString());
            lock (_timeline) Assert.Equal(new[] { "result:42:0", "power:Logoff" }, _timeline);
        }

        [Fact]
        public async Task Power_CancelTask_DuringTheCountdown_StopsIt()
        {
            UseManualDelay();
            await Handle(Power(43, "shutdown", 120));
            Assert.Single(Tray(PowerActions.CountdownPrefix));

            await Handle("{\"action\":\"cancel_task\",\"task_id\":43}");
            JsonElement result = await WaitForResult(43);
            Assert.Equal(CommandRunner.ExitCancelled, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith("[İPTAL EDİLDİ]", result.GetProperty("output").GetString());
            await WaitUntilPowerIdle();
            Assert.Contains(PowerActions.CancelPrefix + "43", _tray);

            // Süre sonradan dolsa da bilgisayara dokunulmaz
            _release.SetResult();
            await Task.Delay(100);
            Assert.Empty(PowerCalls());
            Assert.Single(Results(43));
        }

        [Fact]
        public async Task Power_SameTaskIdAgain_DuringTheCountdown_IsIgnored()
        {
            UseManualDelay();
            await Handle(Power(44, "restart", 90));
            await Handle(Power(44, "restart", 90));
            Assert.Single(Tray(PowerActions.CountdownPrefix));
            Assert.Single(Audits(1130));
            Assert.Empty(_sent);

            _release.SetResult();
            await WaitUntilPowerIdle();
            Assert.Single(PowerCalls());
            Assert.Single(Results(44));
        }

        [Fact]
        public async Task Power_NewerRequest_ReplacesTheRunningCountdown()
        {
            SessionTasks.Delay = (time, token) => time == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(Timeout.Infinite, token);
            await Handle(Power(45, "restart", 300));
            await Handle(Power(46, "shutdown", 0));

            JsonElement replaced = await WaitForResult(45);
            Assert.Equal(CommandRunner.ExitCancelled, replaced.GetProperty("exit_code").GetInt32());
            Assert.Contains("görev 46", replaced.GetProperty("output").GetString());
            await WaitFor(() => PowerCalls().Count == 1, "Yeni güç işlemi yapılmadı");
            await WaitUntilPowerIdle();
            Assert.Equal((PowerOperation.Shutdown, 46), Assert.Single(PowerCalls()));
            Assert.Equal(0, Assert.Single(Results(46)).GetProperty("exit_code").GetInt32());
        }

        [Fact]
        public async Task Power_ServiceStopping_DuringTheCountdown_IsMinusFour()
        {
            UseManualDelay();
            using var stopping = new CancellationTokenSource();
            await _worker.HandleServerMessageAsync(Power(47, "restart", 60), null, stopping.Token);
            stopping.Cancel();
            JsonElement result = await WaitForResult(47);
            Assert.Equal(CommandRunner.ExitServiceStopping, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith("[DURDURULDU]", result.GetProperty("output").GetString());
            await WaitUntilPowerIdle();
            Assert.Empty(PowerCalls());
        }

        [Fact]
        public async Task Power_UserSignedOutDuringTheCountdown_IsMinusSix()
        {
            UseManualDelay();
            await Handle(Power(48, "logoff", 30));
            _consoleUser = false;
            _release.SetResult();
            JsonElement result = await WaitForResult(48);
            Assert.Equal(SessionTasks.ExitNoUser, result.GetProperty("exit_code").GetInt32());
            await WaitUntilPowerIdle();
            Assert.Empty(PowerCalls());
            Assert.Contains(PowerActions.CancelPrefix + "48", _tray);
        }

        [Fact]
        public async Task Power_TrayReconnects_SeesTheRemainingCountdown()
        {
            UseManualDelay();
            await Handle(Power(49, "shutdown", 300, ",\"message\":\"Elektrik kesintisi\""));
            lock (_timeline) _tray.Clear();

            _worker.Power.SyncTray();
            JsonElement countdown = Decode(Assert.Single(Tray(PowerActions.CountdownPrefix)), PowerActions.CountdownPrefix);
            Assert.Equal(49, countdown.GetProperty("task_id").GetInt32());
            Assert.InRange(countdown.GetProperty("seconds").GetInt32(), 290, 300);
            Assert.Equal("Elektrik kesintisi", countdown.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Power_ActionThatFails_IsLoggedAndTheResultStays()
        {
            TestEnvironment.IsolatePowerAndSessions();   // gerçek API yerine hata veren sahte
            SessionTasks.HasConsoleUser = () => true;
            await Handle(Power(50, "restart"));
            await WaitUntilPowerIdle();
            Assert.Equal(0, Assert.Single(Results(50)).GetProperty("exit_code").GetInt32());
        }

        // ------------------------------------------------------------------ güç: kilitleme yolu

        [Fact]
        public async Task Power_Lock_GoesThroughTheTrayInTheConsoleSession()
        {
            _trayInConsole = true;
            _onTray = message =>
            {
                if (message.StartsWith(PowerActions.LockPrefix, StringComparison.Ordinal))
                    _ = _worker.OnTrayReplyAsync(PowerActions.LockResultPrefix + message.Substring(PowerActions.LockPrefix.Length) + ":1");
            };
            await Handle(Power(51, "lock"));
            await WaitUntilPowerIdle();
            Assert.Equal(PowerActions.LockPrefix + "51", Assert.Single(Tray(PowerActions.LockPrefix)));
            Assert.Empty(PowerCalls());
            Assert.Equal("[TAMAM] Bilgisayar kilitleniyor.", Assert.Single(Results(51)).GetProperty("output").GetString());
        }

        [Fact]
        public async Task Power_Lock_FallsBackToTheServiceWhenTheTrayCannotLock()
        {
            _trayInConsole = true;
            _onTray = message =>
            {
                if (message.StartsWith(PowerActions.LockPrefix, StringComparison.Ordinal))
                    _ = _worker.OnTrayReplyAsync(PowerActions.LockResultPrefix + message.Substring(PowerActions.LockPrefix.Length) + ":0");
            };
            await Handle(Power(52, "lock"));
            await WaitUntilPowerIdle();
            Assert.Equal((PowerOperation.Lock, 52), Assert.Single(PowerCalls()));
        }

        [Fact]
        public async Task Power_Lock_FallsBackToTheServiceWhenTheTrayDoesNotAnswer()
        {
            _trayInConsole = true;
            PowerActions.TrayLockTimeout = TimeSpan.FromMilliseconds(50);
            await Handle(Power(53, "lock"));
            await WaitUntilPowerIdle();
            Assert.Single(Tray(PowerActions.LockPrefix));
            Assert.Equal((PowerOperation.Lock, 53), Assert.Single(PowerCalls()));
            // Geç gelen yanıt bir şey değiştirmez
            Assert.False(await _worker.OnTrayReplyAsync(PowerActions.LockResultPrefix + "53:1"));
        }

        [Fact]
        public async Task Power_Lock_WithoutTheTrayInTheConsoleSession_UsesTheService()
        {
            _trayInConsole = false;
            await Handle(Power(54, "lock"));
            await WaitUntilPowerIdle();
            Assert.Empty(Tray(PowerActions.LockPrefix));
            Assert.Equal((PowerOperation.Lock, 54), Assert.Single(PowerCalls()));
        }

        // ------------------------------------------------------------------ kullanıcı mesajı

        [Fact]
        public async Task UserMessage_WithoutAck_IsShownAndReportedAtOnce()
        {
            const string text = "Yarın sınav var.\r\nLaboratuvar 09:00'da açılır.\u0007";
            const string cleaned = "Yarın sınav var.\nLaboratuvar 09:00'da açılır.";
            await Handle($"{{\"action\":\"user_message\",\"task_id\":61,\"title\":\"Duyuru\\u001B\",\"text\":{JsonSerializer.Serialize(text)},\"style\":\"warning\",\"requires_ack\":false,\"requested_by\":\"Pasha\"}}");

            JsonElement shown = Decode(Assert.Single(Tray(UserMessages.ShowPrefix)), UserMessages.ShowPrefix);
            Assert.Equal(61, shown.GetProperty("task_id").GetInt32());
            Assert.Equal("Duyuru", shown.GetProperty("title").GetString());
            Assert.Equal(cleaned, shown.GetProperty("text").GetString());
            Assert.Equal("warning", shown.GetProperty("style").GetString());
            Assert.False(shown.GetProperty("requires_ack").GetBoolean());

            JsonElement result = Assert.Single(Results(61));
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal("[TAMAM] gösterildi", result.GetProperty("output").GetString());

            LocalAuditEvent audit = Assert.Single(Audits(1140));
            Assert.Contains("task_id: 61", audit.Message);
            Assert.Contains("title_length: 6", audit.Message);
            Assert.Contains($"text_length: {cleaned.Length}", audit.Message);
            Assert.Contains("style: warning", audit.Message);
            Assert.Contains("requires_ack: False", audit.Message);
            Assert.Contains("requested_by: Pasha", audit.Message);
            Assert.DoesNotContain("sınav", audit.Message);
            Assert.DoesNotContain("Duyuru", audit.Message);
            Assert.Empty(Audits(1141));
        }

        // requested_by isteğe bağlıdır (şemada zorunlu değil)
        [Fact]
        public async Task UserMessage_WithoutRequestedBy_IsShown()
        {
            await Handle(MessageWith(62, "requested_by", null));
            JsonElement shown = Decode(Assert.Single(Tray(UserMessages.ShowPrefix)), UserMessages.ShowPrefix);
            Assert.Equal("Duyuru", shown.GetProperty("title").GetString());
            Assert.Equal("info", shown.GetProperty("style").GetString());
            Assert.False(shown.GetProperty("requires_ack").GetBoolean());
            Assert.Equal(UserMessages.ShownOutput, Assert.Single(Results(62)).GetProperty("output").GetString());
        }

        [Fact]
        public async Task UserMessage_Acknowledged_ReportsRead()
        {
            UseManualDelay();
            await Handle(Message(63, "Lütfen okuyun: gizli-metin-63", true));
            Assert.True(Decode(Assert.Single(Tray(UserMessages.ShowPrefix)), UserMessages.ShowPrefix).GetProperty("requires_ack").GetBoolean());
            await WaitFor(() => Delays().Count == 1, "Onay beklemesi başlamadı");
            Assert.Equal(UserMessages.AckTimeout, Assert.Single(Delays()));
            Assert.Empty(Results(63));   // sonuç tek: okundu ya da onaylanmadı

            Assert.True(await _worker.OnTrayReplyAsync(UserMessages.AckPrefix + "63"));
            JsonElement result = Assert.Single(Results(63));
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal("[TAMAM] okundu", result.GetProperty("output").GetString());
            Assert.Contains("outcome: acknowledged", Assert.Single(Audits(1141)).Message);

            // İkinci onay ve süre sonu bir şey göndermez
            Assert.False(await _worker.OnTrayReplyAsync(UserMessages.AckPrefix + "63"));
            _release.SetResult();
            await Task.Delay(100);
            Assert.Single(Results(63));
            lock (_timeline) Assert.DoesNotContain(_audit, a => a.Message.Contains("gizli-metin"));
        }

        [Fact]
        public async Task UserMessage_NotAcknowledgedInTime_ReportsShownNotAcknowledged()
        {
            UserMessages.AckTimeout = TimeSpan.FromMinutes(30);
            await Handle(Message(64, "Okundu onayı istenen mesaj", true));
            JsonElement result = await WaitForResult(64);
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal("[TAMAM] gösterildi, onaylanmadı", result.GetProperty("output").GetString());
            Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(Delays()));
            await WaitFor(() => Audits(1141).Count == 1, "1141 yazılmadı");
            Assert.Contains("outcome: timeout", Audits(1141).Single().Message);

            // Geç gelen onay yok sayılır
            Assert.False(await _worker.OnTrayReplyAsync(UserMessages.AckPrefix + "64"));
            Assert.Single(Results(64));
        }

        [Fact]
        public async Task UserMessage_SameTaskIdAgain_WhileWaiting_IsIgnored()
        {
            UseManualDelay();
            await Handle(Message(65, "Bir kez", true));
            await Handle(Message(65, "Bir kez", true));
            Assert.Single(Tray(UserMessages.ShowPrefix));
            Assert.Single(Audits(1140));
            Assert.Empty(_sent);
        }

        [Fact]
        public async Task UserMessage_CancelTask_WithdrawsIt()
        {
            UseManualDelay();
            await Handle(Message(66, "Geri çekilecek", true));
            await Handle("{\"action\":\"cancel_task\",\"task_id\":66}");
            JsonElement result = Assert.Single(Results(66));
            Assert.Equal(CommandRunner.ExitCancelled, result.GetProperty("exit_code").GetInt32());
            Assert.Contains(UserMessages.ClosePrefix + "66", _tray);
            Assert.Contains("outcome: cancelled", Assert.Single(Audits(1141)).Message);
            Assert.False(await _worker.OnTrayReplyAsync(UserMessages.AckPrefix + "66"));
        }

        [Fact]
        public async Task UserMessage_ServiceStopping_WhileWaiting_ReportsNotAcknowledged()
        {
            UseManualDelay();
            using var stopping = new CancellationTokenSource();
            await _worker.HandleServerMessageAsync(Message(67, "Servis duracak", true), null, stopping.Token);
            stopping.Cancel();
            JsonElement result = await WaitForResult(67);
            Assert.Equal(UserMessages.NotAcknowledgedOutput, result.GetProperty("output").GetString());
            await WaitFor(() => Audits(1141).Count == 1, "1141 yazılmadı");
            Assert.Contains("outcome: service_stopping", Audits(1141).Single().Message);
        }

        [Fact]
        public async Task UserMessage_TrayReconnects_SeesWaitingMessagesAgain()
        {
            UseManualDelay();
            await Handle(Message(68, "Bekleyen", true));
            await Handle(Message(69, "Bitmiş", false));
            lock (_timeline) _tray.Clear();

            _worker.Messages.SyncTray();
            JsonElement again = Decode(Assert.Single(Tray(UserMessages.ShowPrefix)), UserMessages.ShowPrefix);
            Assert.Equal(68, again.GetProperty("task_id").GetInt32());
        }

        [Fact]
        public async Task TrayReply_Unknown_IsIgnored()
        {
            Assert.False(await _worker.OnTrayReplyAsync(UserMessages.AckPrefix + "x"));
            Assert.False(await _worker.OnTrayReplyAsync(UserMessages.AckPrefix + "999"));
            Assert.False(await _worker.OnTrayReplyAsync(PowerActions.LockResultPrefix + "999:1"));
            Assert.False(await _worker.OnTrayReplyAsync("BAŞKA:1"));
            Assert.Empty(_sent);
        }

        // ------------------------------------------------------------------ Olay Günlüğü kayıtları

        [Fact]
        public void AuditEvents_HaveTheContractIdsAndOnlyMetadata()
        {
            LocalAuditEvent power = LocalAudit.PowerAccepted("shutdown", 600, "Pasha\r\nforged", 5);
            LocalAuditEvent shown = LocalAudit.UserMessageShown(6, 12, 345, "info", true, "Pasha");
            LocalAuditEvent finished = LocalAudit.UserMessageFinished(6, "timeout");

            Assert.Equal((1130, LocalAuditLevel.Information), (power.EventId, power.Level));
            Assert.Equal("Güç işlemi kabul edildi\nop: shutdown\ndelay: 600\nrequested_by: Pasha??forged\ntask_id: 5", power.Message);
            Assert.Equal((1140, LocalAuditLevel.Information), (shown.EventId, shown.Level));
            Assert.Equal("Kullanıcıya mesaj gösterildi\ntask_id: 6\ntitle_length: 12\ntext_length: 345\nstyle: info\nrequires_ack: True\nrequested_by: Pasha", shown.Message);
            Assert.Equal((1141, LocalAuditLevel.Information), (finished.EventId, finished.Level));
            Assert.Equal("Kullanıcı mesajı sonuçlandı\ntask_id: 6\noutcome: timeout", finished.Message);
        }

        // ------------------------------------------------------------------ protokol vektörleri

        public static IEnumerable<object[]> ServerVectors() =>
            Directory.GetFiles(Path.Combine(TestEnvironment.RepoRoot(), "docs", "protocol", "examples", "server-to-agent"), "*.json")
                .Select(path => Path.GetFileName(path))
                .Where(name => name.StartsWith("power.", StringComparison.Ordinal) || name.StartsWith("user_message.", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new object[] { name });

        [Theory]
        [MemberData(nameof(ServerVectors))]
        public async Task ProtocolVectors_AreAccepted(string fileName)
        {
            string json = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "docs", "protocol", "examples", "server-to-agent", fileName), Encoding.UTF8);
            int taskId = JsonDocument.Parse(json).RootElement.GetProperty("task_id").GetInt32();
            _trayInConsole = false;
            await Handle(json);
            await WaitUntilPowerIdle();
            if (fileName.StartsWith("power", StringComparison.Ordinal))
            {
                Assert.Single(PowerCalls());
                Assert.Equal(0, Assert.Single(Results(taskId)).GetProperty("exit_code").GetInt32());
            }
            else
            {
                Assert.Single(Tray(UserMessages.ShowPrefix));
                await WaitFor(() => Results(taskId).Count == 1, "Mesaj sonucu gelmedi");
                Assert.StartsWith("[TAMAM] gösterildi", Results(taskId).Single().GetProperty("output").GetString());
            }
            Assert.Empty(Sent("capability_denied"));
        }
    }
}
