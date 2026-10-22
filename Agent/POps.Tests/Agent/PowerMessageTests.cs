using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Uzaktan güç işlemleri (power) ve kullanıcı mesajları (user_message). Makineye dokunan her şey sahtedir: güç API'si
    // (makine kapısı MachineSeams.Power) çağrıyı kaydeder ve testin sonucunu (_outcome; shutdown.exe ve oturum çağrısının
    // yorumu WindowsPower'ın sahte süreç ve sahte çağrı alan parçalarıyla) döndürür, oturum sorgusu
    // (MachineSeams.HasConsoleUser) testin değeridir, bekleme (PowerActions.Delay, UserMessages.Delay) anında ya da test
    // bırakınca biter, tepsi borusu TrayOverride'dır. Bu testler bilgisayarı asla kapatmaz, yeniden başlatmaz, oturumu
    // kapatmaz ya da kilitlemez.
    public class PowerMessageTests : TestBase, IDisposable
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
        // false: komut bağlantısı yok, gönderim başarısız (hiçbir şey gitmez)
        private bool _online = true;

        // Testin ajanı: güç API'si _power, konsoldaki kullanıcı _consoleUser (makine kapıları)
        private readonly AgentHarness _agent;
        private readonly Func<PowerOperation, int, PowerOutcome> _power;
        // Sahte güç API'sinin sonucu (varsayılan: yapıldı)
        private Func<PowerOperation, int, PowerOutcome> _outcome = (_, _) => PowerOutcome.Done;

        public PowerMessageTests()
        {
            _agent = AgentHarness.Create("power", machine: new MachineSeams
            {
                Kiosk = new FakeKioskRegistry(),
                Power = (operation, taskId) => _power(operation, taskId),
                HasConsoleUser = () => _consoleUser,
            });
            _agent.Capabilities.Load();   // dosya yok: hepsi açık
            _worker = new Worker(NullLogger<Worker>.Instance, _agent.Context)
            {
                HwId = "HW-TEST",
                SendOverride = payload =>
                {
                    if (!_online) return Task.FromResult(false);
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
            _power = (operation, taskId) =>
            {
                lock (_timeline)
                {
                    _powerCalls.Add((operation, taskId));
                    _timeline.Add("power:" + operation);
                }
                return _outcome(operation, taskId);
            };
            UseInstantDelay();
        }

        // Bırakılmamış beklemeler (_release) sonsuza dek bekler: süren bir geri sayım bir sonraki testin sahtesini çağıramaz
        public void Dispose()
        {
            _worker.Dispose();
            _agent.Dispose();
        }

        private void Record(LocalAuditEvent item)
        {
            lock (_timeline) _audit.Add(item);
        }

        // Geri sayım ve okundu onayı beklemesi (güç işlemleri ve kullanıcı mesajları aynı bekleme girişini kullanır)
        private void UseDelay(Func<TimeSpan, CancellationToken, Task> delay)
        {
            _worker.Power.Delay = delay;
            _worker.Messages.Delay = delay;
        }

        private void UseInstantDelay() => UseDelay((time, token) =>
        {
            lock (_delays) _delays.Add(time);
            return Task.CompletedTask;
        });

        // Bekleme test _release'i tamamlayana (ya da iptal edilene) kadar sürer
        private void UseManualDelay() => UseDelay((time, token) =>
        {
            lock (_delays) _delays.Add(time);
            return _release.Task.WaitAsync(token);
        });

        // X-Agent-Features: bu bağlamın yetenek durumundan (bkz. CommandConnection)
        private AgentFeatures AgentFeatures => new AgentFeatures(_agent.Capabilities);

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

        private void WriteCapabilities(string json)
        {
            File.WriteAllText(_agent.Paths.SecureFile(AgentCapabilities.FileName), json);
            _agent.Capabilities.Load();
        }

        // server_info'su result_ack duyuran (sonuçları onaylayan) sunucuya bağlanmış bir handshake
        internal static ServerHandshake Connected(ServerHandshake handshake)
        {
            handshake.OnConnected();
            handshake.OnServerInfo(JsonDocument.Parse("{\"action\":\"server_info\",\"features\":[\"result_ack\"]}").RootElement);
            return handshake;
        }

        // Bağlantının sunucusu result_ack duyurdu (sonuçlar onaya kadar diskte). Duyurmayan sunucu varsayılandır: bu Worker hiç
        // bağlanmadı, sunucu eski sayılır.
        private void UseAckServer() => Connected(_worker.Handshake);

        private string SpoolPath => _agent.Paths.SecureFile(ResultSpool.FileName);

        // Diskteki (onay bekleyen ya da gönderilmemiş) sonuçlar, dosyadan yeniden okunarak (servis yeniden başlamış gibi)
        private List<ResultSpool.Entry> OnDisk() => new ResultSpool(SpoolPath).All();

        private static string Output(JsonElement result) => result.GetProperty("output").GetString();

        private static int ExitCode(JsonElement result) => result.GetProperty("exit_code").GetInt32();

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
            string worker = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "Agent", "POps.Agent", "POps.Agent", "CommandConnection.cs"));
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
            Assert.False(_agent.Capabilities.PowerEnabled);
            Assert.False(_agent.Capabilities.MessageEnabled);
            Assert.True(_agent.Capabilities.TerminalEnabled);
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
            Assert.False(_agent.Capabilities.PowerEnabled);
            _agent.Capabilities.Load();
            Assert.False(_agent.Capabilities.PowerEnabled);
            Assert.False(_agent.Capabilities.MessageEnabled);
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
            Assert.True(_agent.Capabilities.PowerEnabled);
            Assert.True(_agent.Capabilities.MessageEnabled);
            Assert.False(_agent.Capabilities.TerminalEnabled);

            WriteCapabilities("{ bozuk");
            Assert.False(_agent.Capabilities.PowerEnabled);
            Assert.False(_agent.Capabilities.MessageEnabled);
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
        public async Task Power_Countdown_TrayShowsIt_ResultFollowsTheAction()
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
            // Sonuç shutdown.exe'nin çıkış kodu belli olunca gider (önce yalnızca diske yazılır)
            lock (_timeline) Assert.Equal(new[] { "power:Restart", "result:41:0" }, _timeline);

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
            // Oturum kapatma bağlantıyı kesmez: önce işlem, sonra onun sonucu
            lock (_timeline) Assert.Equal(new[] { "power:Logoff", "result:42:0" }, _timeline);
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
            UseDelay((time, token) => time == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(Timeout.Infinite, token));
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

        // Makine kapısı hata atarsa (burada varsayılanı: gerçek API yerine hata veren sahte) işlem denenememiştir: -3 ve
        // "[HATA]"; diske önceden yazılan "[TAMAM]" hiç gitmez
        [Theory]
        [InlineData("restart")]
        [InlineData("lock")]
        public async Task Power_SeamThatThrows_IsAnAgentError(string op)
        {
            _outcome = new MachineSeams().Power;
            await Handle(Power(50, op));
            await WaitUntilPowerIdle();
            JsonElement result = Assert.Single(Results(50));
            Assert.Equal(CommandRunner.ExitAgentError, ExitCode(result));
            Assert.StartsWith("[HATA] ", Output(result));
            Assert.Contains("güç işlemi yürütülemedi: Testte gerçek güç API'si çağrıldı", Output(result));
            Assert.Single(_sent);
            Assert.Empty(OnDisk());
        }

        // ------------------------------------------------------------------ güç: kapatma ve yeniden başlatma

        // Sonuç shutdown.exe çağrılmadan önce diskte, gönderilmemiş (heartbeat de göndermez); çıkış kodu 0 olunca gönderilir.
        // Onaylı sunucuda onaya kadar diskte kalır, onaysızda gönderilince silinir.
        [Theory]
        [InlineData("shutdown", PowerOperation.Shutdown, false)]
        [InlineData("restart", PowerOperation.Restart, false)]
        [InlineData("shutdown", PowerOperation.Shutdown, true)]
        [InlineData("restart", PowerOperation.Restart, true)]
        public async Task Power_ShutdownOrRestart_ResultIsOnDiskBeforeShutdownExe(string op, PowerOperation operation, bool ackServer)
        {
            if (ackServer) UseAckServer();
            List<ResultSpool.Entry> onDiskAtCall = null;
            int sentAtCall = -1;
            ProcessStartInfo started = null;
            _outcome = (o, taskId) => WindowsPower.ShutdownExe(WindowsPower.ShutdownStart(o == PowerOperation.Restart, taskId), start =>
            {
                // shutdown.exe çalışırken bir heartbeat de gelir: saklanan sonuç gönderilmez
                _worker.FlushPendingResultsAsync().GetAwaiter().GetResult();
                onDiskAtCall = OnDisk();
                sentAtCall = Sent("result").Count;
                started = start;
                return 0;
            });
            await Handle(Power(71, op));
            await WaitUntilPowerIdle();

            Assert.Equal((operation, 71), Assert.Single(PowerCalls()));
            Assert.Equal(operation == PowerOperation.Restart ? "/r" : "/s", started.ArgumentList[0]);
            ResultSpool.Entry stored = Assert.Single(onDiskAtCall);
            Assert.Equal(71, stored.TaskId);
            Assert.Equal(PowerActions.DoneOutput(operation), Output(stored.Result));
            Assert.Equal(0, ExitCode(stored.Result));
            Assert.Equal(0, sentAtCall);

            JsonElement result = Assert.Single(Results(71));
            Assert.Equal(0, ExitCode(result));
            Assert.Equal(PowerActions.DoneOutput(operation), Output(result));
            lock (_timeline) Assert.Equal(new[] { "power:" + operation, "result:71:0" }, _timeline);
            if (ackServer)
            {
                Assert.Equal(new[] { 71 }, OnDisk().Select(e => e.TaskId));
                await Handle("{\"action\":\"result_ack\",\"task_id\":71}");
            }
            Assert.Empty(OnDisk());
        }

        // shutdown.exe 0 dışında bir kodla döndü (ör. politika kapatmayı engelledi): diskteki "[TAMAM]" "[HATA] shutdown.exe
        // <kod>: ..." ile değiştirilir ve hemen gönderilir; bilgisayar açık kalır, başka hiçbir şey yapılmaz
        [Theory]
        [InlineData("shutdown", PowerOperation.Shutdown, "Bilgisayar kapatılmadı.")]
        [InlineData("restart", PowerOperation.Restart, "Bilgisayar yeniden başlatılmadı.")]
        public async Task Power_ShutdownExeFails_TheErrorIsSentAndThePcStaysOn(string op, PowerOperation operation, string tail)
        {
            UseAckServer();
            const int blocked = 1190;   // ERROR_SHUTDOWN_IS_SCHEDULED
            _outcome = (o, taskId) => WindowsPower.ShutdownExe(WindowsPower.ShutdownStart(o == PowerOperation.Restart, taskId), _ => blocked);
            await Handle(Power(72, op, extra: ",\"message\":\"Ders bitti\""));
            await WaitUntilPowerIdle();

            Assert.Equal((operation, 72), Assert.Single(PowerCalls()));
            JsonElement result = Assert.Single(Results(72));
            Assert.Equal(blocked, ExitCode(result));
            Assert.Equal($"[HATA] shutdown.exe 1190: {WindowsPower.Win32Message(blocked)}. {tail}", Output(result));
            // Sunucuya giden tek mesaj bu sonuçtur: "[TAMAM]" hiç gitmedi
            Assert.Single(_sent);
            ResultSpool.Entry stored = Assert.Single(OnDisk());
            Assert.Equal(blocked, ExitCode(stored.Result));
            Assert.StartsWith("[HATA] shutdown.exe 1190: ", Output(stored.Result));
            await Handle("{\"action\":\"result_ack\",\"task_id\":72}");
            Assert.Empty(OnDisk());

            // Sonraki heartbeat'ler de bir şey göndermez; güç API'si yeniden çağrılmaz
            await _worker.FlushPendingResultsAsync();
            Assert.Single(_sent);
            Assert.Single(PowerCalls());
            Assert.Empty(_tray);
        }

        // shutdown.exe kapatmayı başlattı ama sonuç gönderilemedi (bağlantı yok): diskte kalır ve yeniden açılışta (yeni
        // servis, aynı dosya) gönderilir
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Power_Restart_ResultThatCannotBeSent_GoesAfterBoot(bool ackServer)
        {
            if (ackServer) UseAckServer();
            _online = false;
            await Handle(Power(73, "restart"));
            await WaitUntilPowerIdle();
            Assert.Equal((PowerOperation.Restart, 73), Assert.Single(PowerCalls()));
            Assert.Empty(_sent);
            Assert.Equal(new[] { 73 }, OnDisk().Select(e => e.TaskId));

            // Açılış: dosya yeniden okunur; sunucu ilk bağlantıda kendini duyurur (ya da eski sunucu duyurmaz)
            var sent = new List<JsonElement>();
            var afterBoot = new ResultOutbox(ackServer ? Connected(new ServerHandshake()) : new ServerHandshake(), new ResultSpool(SpoolPath), payload =>
            {
                sent.Add(JsonSerializer.SerializeToElement(payload));
                return Task.FromResult(true);
            });
            await afterBoot.FlushPendingResultsAsync();
            JsonElement result = Assert.Single(sent);
            Assert.Equal(73, result.GetProperty("task_id").GetInt32());
            Assert.Equal(0, ExitCode(result));
            Assert.Equal("[TAMAM] Bilgisayar yeniden başlatılıyor.", Output(result));
            Assert.Equal("HW-TEST", result.GetProperty("pc_name").GetString());
            Assert.Equal(ackServer ? 1 : 0, OnDisk().Count);
        }

        // ------------------------------------------------------------------ güç: kilitleme ve oturum kapatma

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
            JsonElement result = Assert.Single(Results(51));
            Assert.Equal(0, ExitCode(result));
            Assert.Equal(PowerActions.LockedOutput, Output(result));
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
            JsonElement result = Assert.Single(Results(52));
            Assert.Equal(0, ExitCode(result));
            Assert.Equal(PowerActions.DisconnectedOutput, Output(result));
        }

        [Fact]
        public async Task Power_Lock_FallsBackToTheServiceWhenTheTrayDoesNotAnswer()
        {
            _trayInConsole = true;
            _worker.Power.TrayLockTimeout = TimeSpan.FromMilliseconds(50);
            await Handle(Power(53, "lock"));
            await WaitUntilPowerIdle();
            Assert.Single(Tray(PowerActions.LockPrefix));
            Assert.Equal((PowerOperation.Lock, 53), Assert.Single(PowerCalls()));
            Assert.Equal(PowerActions.DisconnectedOutput, Output(Assert.Single(Results(53))));
            // Geç gelen yanıt bir şey değiştirmez
            Assert.False(await _worker.OnTrayReplyAsync(PowerActions.LockResultPrefix + "53:1"));
            Assert.Single(Results(53));
        }

        [Fact]
        public async Task Power_Lock_WithoutTheTrayInTheConsoleSession_UsesTheService()
        {
            _trayInConsole = false;
            await Handle(Power(54, "lock"));
            await WaitUntilPowerIdle();
            Assert.Empty(Tray(PowerActions.LockPrefix));
            Assert.Equal((PowerOperation.Lock, 54), Assert.Single(PowerCalls()));
            Assert.Equal(PowerActions.DisconnectedOutput, Output(Assert.Single(Results(54))));
        }

        // Kabulden sonra konsol oturumu kalmadı (ör. oturum değişirken): çağrı yapılmaz, sonuç hata (-6 değil: kabulde
        // kullanıcı vardı)
        [Fact]
        public async Task Power_Lock_NoConsoleSessionAtTheCall_IsAnError()
        {
            int calls = 0;
            _outcome = (_, _) => WindowsPower.OnConsoleSession(() => UserSessionLauncher.NoSession, session =>
            {
                calls++;
                return (true, 0);
            }, "WTSDisconnectSession");
            await Handle(Power(55, "lock"));
            await WaitUntilPowerIdle();
            JsonElement result = Assert.Single(Results(55));
            Assert.Equal(CommandRunner.ExitAgentError, ExitCode(result));
            Assert.Equal("[HATA] Bilgisayar kilitlenemedi: WTSDisconnectSession çağrılmadı: etkin konsol oturumu yok.", Output(result));
            Assert.Equal(0, calls);
        }

        // WTSDisconnectSession ya da WTSLogoffSession hata döndü: sonuç Win32 hata kodu ve "[HATA] ..."
        [Theory]
        [InlineData("lock", "WTSDisconnectSession", "[HATA] Bilgisayar kilitlenemedi: ")]
        [InlineData("logoff", "WTSLogoffSession", "[HATA] Kullanıcının oturumu kapatılamadı: ")]
        public async Task Power_LockOrLogoff_CallFails_ReportsTheWin32Error(string op, string call, string prefix)
        {
            const int accessDenied = 5;
            uint calledWith = 0;
            _outcome = (_, _) => WindowsPower.OnConsoleSession(() => 3, session =>
            {
                calledWith = session;
                return (false, accessDenied);
            }, call);
            await Handle(Power(56, op));
            await WaitUntilPowerIdle();
            Assert.Equal(3u, calledWith);
            JsonElement result = Assert.Single(Results(56));
            Assert.Equal(accessDenied, ExitCode(result));
            Assert.Equal($"{prefix}{call} başarısız (oturum 3, Win32 5: {WindowsPower.Win32Message(accessDenied)}).", Output(result));
        }

        // Tepsi kilitlemeyi onaylamadı ("0" ya da süre içinde yanıt yok) ve servisin oturum ayırması da başarısız: hata, kod
        // ayırma çağrısınınki
        [Theory]
        [InlineData(true, "tepside LockWorkStation başarısız")]
        [InlineData(false, "tepsi 0.05 sn içinde yanıt vermedi")]
        public async Task Power_Lock_TrayDidNotConfirmAndTheFallbackFailed_IsAnError(bool trayAnswers, string trayProblem)
        {
            _trayInConsole = true;
            _worker.Power.TrayLockTimeout = TimeSpan.FromMilliseconds(50);
            if (trayAnswers)
                _onTray = message =>
                {
                    if (message.StartsWith(PowerActions.LockPrefix, StringComparison.Ordinal))
                        _ = _worker.OnTrayReplyAsync(PowerActions.LockResultPrefix + message.Substring(PowerActions.LockPrefix.Length) + ":0");
                };
            _outcome = (_, _) => WindowsPower.OnConsoleSession(() => 1, _ => (false, 7022), "WTSDisconnectSession");
            await Handle(Power(57, "lock"));
            await WaitUntilPowerIdle();
            Assert.Single(Tray(PowerActions.LockPrefix));
            Assert.Equal((PowerOperation.Lock, 57), Assert.Single(PowerCalls()));
            JsonElement result = Assert.Single(Results(57));
            Assert.Equal(7022, ExitCode(result));
            Assert.StartsWith($"[HATA] Bilgisayar kilitlenemedi: {trayProblem}; WTSDisconnectSession başarısız (oturum 1, Win32 7022: ", Output(result));
            Assert.False(await _worker.OnTrayReplyAsync(PowerActions.LockResultPrefix + "57:1"));
            Assert.Single(Results(57));
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
            Assert.Equal(_worker.Messages.AckTimeout, Assert.Single(Delays()));
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
            _worker.Messages.AckTimeout = TimeSpan.FromMinutes(30);
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

    // Gerçek güç işlemlerinin sonucu nasıl yorumladığı (WindowsPower): shutdown.exe'nin çıkış kodu ve konsol oturumu çağrısının
    // dönüşü. Süreç ve çağrı testin sahtesidir: hiçbir süreç başlatılmaz, hiçbir oturum ayrılmaz ya da kapatılmaz.
    public class WindowsPowerTests : TestBase
    {
        [Theory]
        [InlineData(false, "/s")]
        [InlineData(true, "/r")]
        public void ShutdownStart_IsShutdownExeFromSystem32_WithTheReason(bool restart, string mode)
        {
            ProcessStartInfo start = WindowsPower.ShutdownStart(restart, 42);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), start.FileName);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Equal(new[] { mode, "/t", "0", "/f", "/d", "p:0:0", "/c", "POps: yönetici isteği (görev 42)" }, start.ArgumentList);
        }

        [Fact]
        public void ShutdownExe_ExitCodeZero_IsDone()
        {
            ProcessStartInfo given = WindowsPower.ShutdownStart(true, 1);
            ProcessStartInfo ran = null;
            PowerOutcome outcome = WindowsPower.ShutdownExe(given, start =>
            {
                ran = start;
                return 0;
            });
            Assert.Same(given, ran);
            Assert.True(outcome.Succeeded);
            Assert.Equal(0, outcome.Code);
            Assert.Equal("", outcome.Error);
        }

        [Theory]
        [InlineData(1190)]   // ERROR_SHUTDOWN_IS_SCHEDULED
        [InlineData(5)]      // erişim engellendi (ör. politika)
        [InlineData(1)]
        public void ShutdownExe_OtherExitCode_IsThatCode(int exitCode)
        {
            PowerOutcome outcome = WindowsPower.ShutdownExe(WindowsPower.ShutdownStart(false, 1), _ => exitCode);
            Assert.False(outcome.Succeeded);
            Assert.Equal(exitCode, outcome.Code);
            Assert.Equal($"shutdown.exe {exitCode}: {WindowsPower.Win32Message(exitCode)}", outcome.Error);
            Assert.False(outcome.Error.EndsWith(".", StringComparison.Ordinal));
        }

        [Fact]
        public void ShutdownExe_ThatDoesNotFinish_OrDoesNotStart_IsAnError()
        {
            PowerOutcome hung = WindowsPower.ShutdownExe(WindowsPower.ShutdownStart(false, 1), _ => null);
            Assert.Equal(CommandRunner.ExitAgentError, hung.Code);
            Assert.Equal($"shutdown.exe {WindowsPower.ShutdownExeWaitSeconds} sn içinde bitmedi", hung.Error);

            PowerOutcome missing = WindowsPower.ShutdownExe(WindowsPower.ShutdownStart(false, 1), _ => throw new Win32Exception(2));
            Assert.Equal(2, missing.Code);
            Assert.StartsWith("shutdown.exe başlatılamadı (Win32 2: ", missing.Error);

            PowerOutcome noProcess = WindowsPower.ShutdownExe(WindowsPower.ShutdownStart(false, 1), _ => throw new InvalidOperationException("süreç başlatılamadı"));
            Assert.Equal(CommandRunner.ExitAgentError, noProcess.Code);
            Assert.Equal("shutdown.exe başlatılamadı (süreç başlatılamadı)", noProcess.Error);
        }

        [Fact]
        public void OnConsoleSession_CallsWithTheConsoleSession()
        {
            uint calledWith = 0;
            PowerOutcome outcome = WindowsPower.OnConsoleSession(() => 7, session =>
            {
                calledWith = session;
                return (true, 0);
            }, "WTSLogoffSession");
            Assert.True(outcome.Succeeded);
            Assert.Equal(7u, calledWith);
        }

        [Fact]
        public void OnConsoleSession_WithoutAConsoleSession_DoesNotCall()
        {
            bool called = false;
            PowerOutcome outcome = WindowsPower.OnConsoleSession(() => UserSessionLauncher.NoSession, _ =>
            {
                called = true;
                return (true, 0);
            }, "WTSDisconnectSession");
            Assert.False(called);
            Assert.Equal(CommandRunner.ExitAgentError, outcome.Code);
            Assert.Equal("WTSDisconnectSession çağrılmadı: etkin konsol oturumu yok", outcome.Error);
        }

        [Fact]
        public void OnConsoleSession_FailedCall_IsItsWin32Error_AndNeverZero()
        {
            PowerOutcome denied = WindowsPower.OnConsoleSession(() => 2, _ => (false, 5), "WTSDisconnectSession");
            Assert.Equal(5, denied.Code);
            Assert.Equal($"WTSDisconnectSession başarısız (oturum 2, Win32 5: {WindowsPower.Win32Message(5)})", denied.Error);

            // Hata kodu okunamadıysa da başarı sayılmaz
            PowerOutcome unknown = WindowsPower.OnConsoleSession(() => 2, _ => (false, 0), "WTSDisconnectSession");
            Assert.False(unknown.Succeeded);
            Assert.Equal(CommandRunner.ExitAgentError, unknown.Code);
        }

        [Fact]
        public void PowerOutcome_DefaultIsDone_AndAFailureIsNeverZero()
        {
            Assert.True(default(PowerOutcome).Succeeded);
            Assert.Equal("", default(PowerOutcome).Error);
            Assert.Equal(PowerOutcome.Done, default(PowerOutcome));
            Assert.Equal(CommandRunner.ExitAgentError, PowerOutcome.Failed(0, "x").Code);
            Assert.Equal(1190, PowerOutcome.Failed(1190, "x").Code);
            Assert.Equal("x", PowerOutcome.Failed(1190, "x").Error);
        }

        // Kapatma, yeniden başlatma, oturum kapatma ve kilitlemenin sonuç metinleri
        [Fact]
        public void Outputs_SayWhatHappened()
        {
            Assert.Equal("[TAMAM] Bilgisayar kapatılıyor.", PowerActions.DoneOutput(PowerOperation.Shutdown));
            Assert.Equal("[TAMAM] Bilgisayar yeniden başlatılıyor.", PowerActions.DoneOutput(PowerOperation.Restart));
            Assert.Equal("[TAMAM] Kullanıcının oturumu kapatılıyor.", PowerActions.DoneOutput(PowerOperation.Logoff));
            Assert.Equal(PowerActions.LockedOutput, PowerActions.DoneOutput(PowerOperation.Lock));
            Assert.Equal("[HATA] shutdown.exe 1190: x. Bilgisayar kapatılmadı.", PowerActions.FailedOutput(PowerOperation.Shutdown, "shutdown.exe 1190: x"));
            Assert.Equal("[HATA] shutdown.exe 5: x. Bilgisayar yeniden başlatılmadı.", PowerActions.FailedOutput(PowerOperation.Restart, "shutdown.exe 5: x"));
            Assert.Equal("[HATA] Kullanıcının oturumu kapatılamadı: x.", PowerActions.FailedOutput(PowerOperation.Logoff, "x"));
            Assert.Equal("[HATA] Bilgisayar kilitlenemedi: x.", PowerActions.FailedOutput(PowerOperation.Lock, "x"));
            foreach (string done in new[] { PowerActions.LockedOutput, PowerActions.DisconnectedOutput })
                Assert.StartsWith("[TAMAM] ", done);
        }
    }
}
