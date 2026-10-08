using System;
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
    // Sunucuya komut bağlantısı uçtan uca (Worker.cs bölmesinin a5 adımında CommandConnection ve CommandChannel'a taşındı;
    // bu testler taşımadan önce de aynı sonucu verir): yol ve kimlik başlıkları, ilk mesajların sırası, kapanış kodlarına
    // göre geri çekilme, alma döngüsünün hataları ve doğrudan sokete yazılan mesajlar (önizleme, vision_rejected). Sunucu
    // 127.0.0.1'de sahtedir (FakeCommandServer); tepsi gerektiğinde gerçek boruya bağlanan sahte bir istemcidir.
    // Bağlantı döngüsü heartbeat'ten sonra 5 sn bekler: sunucunun kapattığını ancak o bekleme bitince görür.
    [Collection(SharedStateCollection.Name)]
    public class CommandConnectionTests : SharedStateTestBase, IDisposable
    {
        private const string HwId = "HW-CONN00000001";
        private const string Secret = "connection-test-secret-0123456789";
        private const string EnrollToken = "connection-test-enroll-0123";
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private readonly FakeCommandServer _server = new FakeCommandServer();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Worker _worker;
        private readonly long _logStart;
        private Task _loop;

        public CommandConnectionTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("connection-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("connection-data");
            TrayPipeServer.PipeName = "POpsTrayPipe-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            TrayPipeServer.ClientCheckOverride = _ => null;
            // DNS izleme bağlantı kurulunca başlar: gerçek DNS önbelleği okunmaz
            DnsPolicyMonitor.CacheReader = () => Array.Empty<string>();
            string config = Path.Combine(TestEnvironment.NewDir("connection-config"), "appsettings.json");
            File.WriteAllText(config, "{\"ServerUrl\":\"" + _server.Url + "\"}");
            POpsHelpers.ConfigPaths = new[] { config };
            // Cihaz secret'ı ve enroll jetonu: ikisi de başlık olarak gider
            AgentCredentials.SaveSecret(Secret, HwId);
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName), EnrollToken);
            AgentCapabilities.Load();   // dosya yok: varsayılanlar (Vision açık)
            _worker = new Worker(NullLogger<Worker>.Instance) { HwId = HwId };
            _logStart = LogLength();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop?.Wait(Wait); } catch (AggregateException) { }
            _worker.TrayPipe?.Stop();
            _worker.Dispose();
            _server.Dispose();
            _stop.Dispose();
            DnsPolicyMonitor.Reset();
            DnsPolicyMonitor.CacheReader = DnsWatch.ReadCacheNames;
            POpsHelpers.ConfigPaths = new string[0];
            TrayPipeServer.PipeName = "POpsTrayPipe";
            TrayPipeServer.ClientCheckOverride = null;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentCapabilities.Load();
        }

        // Bağlantı döngüsü başlar; ajanın ilk bağlantısı
        private async Task<FakeCommandServer.Connection> StartAsync()
        {
            _loop = _worker.RunCommandConnectionAsync(_stop.Token);
            return await _server.NextConnectionAsync(Wait);
        }

        private static string TypeOf(JsonElement message) =>
            message.TryGetProperty("type", out JsonElement type) ? type.GetString() : null;

        // İlk mesajlar: dna_payload'lı heartbeat (type yok), ardından capabilities
        private static async Task ExpectFirstMessagesAsync(FakeCommandServer.Connection connection)
        {
            JsonElement first = await connection.NextAsync(Wait);
            Assert.Null(TypeOf(first));
            Assert.Equal(HwId, first.GetProperty("hw_id").GetString());
            Assert.Equal(JsonValueKind.Object, first.GetProperty("dna_payload").ValueKind);
            Assert.Equal("capabilities", TypeOf(await connection.NextAsync(Wait)));
        }

        private static string ServerInfo(params string[] features) =>
            "{\"action\":\"server_info\",\"version\":\"0.1.23-alpha\",\"protocol\":1,\"features\":" + JsonSerializer.Serialize(features) + "}";

        // Bu testin başından beri yazılan log (bütün testler aynı günlük log dosyasına yazar)
        private static string LogPath => POpsHelpers.LogFilePath(DateTime.Now);

        private static long LogLength() => File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;

        private string LogSinceStart()
        {
            if (!File.Exists(LogPath)) return "";
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Min(_logStart, stream.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private async Task<string> WaitForLogAsync(string pattern)
        {
            Assert.True(await FakeTray.WaitUntilAsync(() => Regex.IsMatch(LogSinceStart(), pattern), 10000), "log satırı yok: " + pattern);
            return Regex.Match(LogSinceStart(), pattern).Value;
        }

        [Fact]
        public async Task Connects_ToTheAgentPath_WithVersionFeatureAndAuthHeaders()
        {
            FakeCommandServer.Connection connection = await StartAsync();
            Assert.Equal("/ws/agent/" + HwId, connection.Path);
            Assert.Equal(Worker.AppVersion, connection.Headers["X-Agent-Version"]);
            Assert.Equal(AgentFeatures.Header, connection.Headers[AgentFeatures.HeaderName]);
            Assert.Equal(Secret, connection.Headers["X-Agent-Secret"]);
            Assert.Equal(EnrollToken, connection.Headers["X-Enroll-Token"]);
        }

        // İlk mesaj daima dna_payload'lı heartbeat'tir (sunucu kimliği ondan çözer); capabilities bağlantı başına bir kez,
        // ilk heartbeat'ten sonra. exam_mode duyurmayan sunucuya exam_state gitmez. Bağlantı cihaz secret'ıyla kurulduğu
        // için set_bypass_secret kabul edilir. Bir sonraki tur yalnızca heartbeat'tir.
        [Fact]
        public async Task FirstMessage_IsTheHeartbeat_ThenCapabilitiesOnce_NoExamStateWithoutTheFeature()
        {
            FakeCommandServer.Connection connection = await StartAsync();
            await ExpectFirstMessagesAsync(connection);

            await connection.SendAsync(ServerInfo("result_ack", "update_progress"));
            await connection.SendAsync("{\"action\":\"set_bypass_secret\",\"secret\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\"}");
            Assert.Equal("bypass_secret_ack", TypeOf(await connection.NextAsync(Wait)));

            JsonElement next = await connection.NextAsync(Wait);
            Assert.Null(TypeOf(next));
            Assert.Equal(HwId, next.GetProperty("hw_id").GetString());
        }

        [Fact]
        public async Task ServerInfoWithExamMode_IsAnsweredWithExamState()
        {
            FakeCommandServer.Connection connection = await StartAsync();
            await ExpectFirstMessagesAsync(connection);

            await connection.SendAsync(ServerInfo("result_ack", ExamMode.Feature));
            JsonElement state = await connection.NextAsync(Wait);
            Assert.Equal("exam_state", TypeOf(state));
            Assert.False(state.GetProperty("enabled").GetBoolean());
        }

        // Olağan kapanış: kısa bir beklemeyle (ilk denemede en çok 2 sn) yeniden bağlanılır; yeni bağlantı yine heartbeat ve
        // capabilities ile başlar, exam_state server_info'dan sonra yeniden bildirilir
        [Fact]
        public async Task NormalClose_Reconnects_AndTheNewConnectionStartsOverWithTheHeartbeat()
        {
            FakeCommandServer.Connection first = await StartAsync();
            await ExpectFirstMessagesAsync(first);
            await first.SendAsync(ServerInfo(ExamMode.Feature));
            Assert.Equal("exam_state", TypeOf(await first.NextAsync(Wait)));
            await first.CloseAsync(1000);

            FakeCommandServer.Connection second = await _server.NextConnectionAsync(TimeSpan.FromSeconds(15));
            string line = await WaitForLogAsync(@"Sunucuya [0-9.,]+ sn sonra yeniden bağlanılacak\.");
            Assert.InRange(double.Parse(Regex.Match(line, "[0-9.,]+").Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture), 0, 2);
            Assert.Equal("/ws/agent/" + HwId, second.Path);
            await ExpectFirstMessagesAsync(second);
            await second.SendAsync(ServerInfo(ExamMode.Feature));
            Assert.Equal("exam_state", TypeOf(await second.NextAsync(Wait)));
        }

        // 4401: sunucu kimliği reddetti; en az 60 sn beklenir (log ve Olay Günlüğü)
        [Fact]
        public async Task Close4401_IsAnAuthRejection_AndTheAgentWaitsAtLeastAMinute()
        {
            FakeCommandServer.Connection connection = await StartAsync();
            await ExpectFirstMessagesAsync(connection);
            await connection.CloseAsync(ReconnectBackoff.AuthRejectedCode);

            string line = await WaitForLogAsync(@"\[GÜVENLİK\] Sunucu ajan kimliğini reddetti \(4401\): geçerli bir enroll jetonu gerekiyor\. \d+ sn sonra yeniden denenecek\.");
            Assert.InRange(int.Parse(Regex.Match(line, @"(\d+) sn sonra").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 60, 62);
            Assert.False(await _server.ConnectsWithinAsync(TimeSpan.FromSeconds(1)));
        }

        // 4409: bu kimlik başka bir bilgisayarda bağlı (kopyalanmış kurulum); en az 10 dk beklenir
        [Fact]
        public async Task Close4409_IsACloneRejection_AndTheAgentWaitsTenMinutes()
        {
            FakeCommandServer.Connection connection = await StartAsync();
            await ExpectFirstMessagesAsync(connection);
            await connection.CloseAsync(ReconnectBackoff.CloneRejectedCode);

            await WaitForLogAsync(@"\[GÜVENLİK\] Sunucu bu cihaz kimliğinin \(" + HwId + @"\) başka bir bilgisayarda bağlı olduğunu bildirdi \(4409\): .* 10[.,]0 dk sonra yeniden denenecek\.");
            Assert.False(await _server.ConnectsWithinAsync(TimeSpan.FromSeconds(1)));
        }

        // Çözümlenemeyen sunucu mesajı alma döngüsünde loglanır; döngü sürer, sonraki mesaj işlenir
        [Fact]
        public async Task InvalidJson_IsLogged_AndTheNextMessageIsStillHandled()
        {
            FakeCommandServer.Connection connection = await StartAsync();
            await ExpectFirstMessagesAsync(connection);

            await connection.SendAsync("{bozuk");
            await connection.SendAsync(ServerInfo(ExamMode.Feature));
            Assert.Equal("exam_state", TypeOf(await connection.NextAsync(Wait)));
            await WaitForLogAsync("Sunucu mesajı çözümlenemedi, yok sayıldı: ");
        }

        // Tepsi oturumu reddetti: vision_rejected o anki komut soketine yazılır (sunucu isteği kapatır)
        [Fact]
        public async Task VisionSessionRejectedInTheTray_IsReportedOnTheCommandSocket()
        {
            FakeCommandServer.Connection connection = await StartAsync();
            await ExpectFirstMessagesAsync(connection);
            using FakeTray tray = await FakeTray.ConnectAsync(TrayPipeServer.PipeName);

            await tray.SendAsync("REJECT_VISION_TUNNEL:SES-5D2C9A1B7E30");
            JsonElement rejected = await connection.NextAsync(Wait);
            Assert.Equal("vision_rejected", TypeOf(rejected));
            Assert.Equal("SES-5D2C9A1B7E30", rejected.GetProperty("session_id").GetString());
            Assert.Equal(HwId, rejected.GetProperty("hw_id").GetString());
        }

        // Ekran önizlemesi: tepsiye CAPTURE_SNAPSHOT, tepsinin JPEG karesi isteğin geldiği sokete thumbnail olarak gider
        [Fact]
        public async Task Thumbnail_IsCapturedByTheTray_AndAnsweredOnTheCommandSocket()
        {
            FakeCommandServer.Connection connection = await StartAsync();
            await ExpectFirstMessagesAsync(connection);
            byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9 };
            using FakeTray tray = await FakeTray.ConnectAsync(TrayPipeServer.PipeName);
            tray.OnMessage = message => message == "CAPTURE_SNAPSHOT" ? tray.SendFrameAsync(jpeg) : Task.CompletedTask;
            Assert.True(await FakeTray.WaitUntilAsync(() => _worker.TrayPipe.IsConnected));

            await connection.SendAsync("{\"type\":\"remote_input\",\"device\":\"" + HwId + "\",\"action\":\"get_thumbnail\"}");
            JsonElement thumbnail = await connection.NextAsync(Wait);
            Assert.Equal("thumbnail", TypeOf(thumbnail));
            Assert.Equal(HwId, thumbnail.GetProperty("hw_id").GetString());
            Assert.Equal(Convert.ToBase64String(jpeg), thumbnail.GetProperty("image").GetString());
            Assert.Contains("CAPTURE_SNAPSHOT", tray.Received);
        }
    }
}
