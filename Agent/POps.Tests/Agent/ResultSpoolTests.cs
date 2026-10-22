using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // B4: görev sonuçlarının disk kuyruğu (result_ack)
    public class ResultSpoolTests : TestBase
    {
        private readonly string _path;

        public ResultSpoolTests()
        {
            _path = AgentHarness.Create("spool").Paths.SecureFile(ResultSpool.FileName);
        }

        private static object Result(int id) => new { type = "result", task_id = id, output = "çıktı " + id, exit_code = 0 };

        [Fact]
        public void AddAndAck_WritesAndDeletesTheFile()
        {
            var spool = new ResultSpool(_path);
            spool.Add(1, Result(1));
            Assert.True(File.Exists(_path));
            Assert.True(spool.Contains(1));
            Assert.False(spool.Remove(2));
            Assert.True(spool.Remove(1));
            Assert.False(File.Exists(_path));
        }

        [Fact]
        public void AtMost20_TheOldestIsDropped()
        {
            var spool = new ResultSpool(_path);
            for (int i = 1; i <= 25; i++) spool.Add(i, Result(i));
            Assert.Equal(ResultSpool.MaxResults, spool.Count);
            Assert.Equal(Enumerable.Range(6, 20), spool.TaskIds());
        }

        [Fact]
        public void TheSameTaskTwice_KeepsOneEntry()
        {
            var spool = new ResultSpool(_path);
            spool.Add(7, Result(7));
            spool.Add(7, new { type = "result", task_id = 7, output = "yeni", exit_code = 1 });
            Assert.Equal(1, spool.Count);
            Assert.Equal("yeni", spool.All()[0].Result.GetProperty("output").GetString());
        }

        [Fact]
        public void ServiceRestart_ReadsTheSpool()
        {
            new ResultSpool(_path).Add(3, Result(3));
            var reloaded = new ResultSpool(_path);
            Assert.Equal(new[] { 3 }, reloaded.TaskIds());
            Assert.Equal("çıktı 3", reloaded.All()[0].Result.GetProperty("output").GetString());
        }

        [Fact]
        public void SentPerConnection_ResetOnReconnect()
        {
            var spool = new ResultSpool(_path);
            spool.Add(1, Result(1));
            spool.Add(2, Result(2));
            spool.MarkSent(1);
            Assert.Equal(new[] { 2 }, spool.Unsent().Select(e => e.TaskId));
            spool.OnConnected();
            Assert.Equal(new[] { 1, 2 }, spool.Unsent().Select(e => e.TaskId));
        }

        [Fact]
        public void UnreadableFile_IsIgnored()
        {
            File.WriteAllText(_path, "{bozuk");
            Assert.Equal(0, new ResultSpool(_path).Count);
        }

        [Fact]
        public void Handshake_UnknownThenKnown_AndRemembersTheLastAnswer()
        {
            DateTime now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            var handshake = new ServerHandshake { UtcNow = () => now };
            Assert.Equal(false, handshake.Supports(ResultSpool.AckFeature));   // hiç bağlanmadı: eski davranış
            handshake.OnConnected();
            Assert.Null(handshake.Supports(ResultSpool.AckFeature));
            handshake.OnServerInfo(JsonDocument.Parse("{\"action\":\"server_info\",\"features\":[\"result_ack\",\"update_result_ack\"]}").RootElement);
            Assert.Equal(true, handshake.Supports(ResultSpool.AckFeature));
            handshake.OnConnected();
            Assert.Null(handshake.Supports(ResultSpool.AckFeature));
            Assert.Equal(true, handshake.LastKnown(ResultSpool.AckFeature));
            now += ServerHandshake.ServerInfoWait;
            Assert.Equal(false, handshake.Supports(ResultSpool.AckFeature));
            Assert.Equal(false, handshake.LastKnown(ResultSpool.AckFeature));
        }
    }

    // Göndermeden saklama (Store) ve saklananı değiştirip gönderme (SendStoredAsync): bağlantıyı kesecek bir güç işleminin
    // (kapatma, yeniden başlatma) sonucu işlemden önce diske yazılır, işlemin gerçek sonucu belli olunca gönderilir
    public class ResultOutboxTests : TestBase, IDisposable
    {
        private readonly AgentHarness _agent = AgentHarness.Create("outbox");
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private DateTime _now = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
        private bool _online = true;

        public void Dispose() => _agent.Dispose();

        private string SpoolPath => _agent.Paths.SecureFile(ResultSpool.FileName);

        // ack: true onaylı sunucu (server_info result_ack), false eski sunucu (hiç bağlanmadı), null bağlantının ilk saniyeleri
        private (ResultOutbox Outbox, ResultSpool Spool, ServerHandshake Handshake) Create(bool? ack)
        {
            var handshake = new ServerHandshake { UtcNow = () => _now };
            if (ack != false) handshake.OnConnected();
            if (ack == true) handshake.OnServerInfo(JsonDocument.Parse("{\"action\":\"server_info\",\"features\":[\"result_ack\"]}").RootElement);
            var spool = new ResultSpool(SpoolPath);
            var outbox = new ResultOutbox(handshake, spool, payload =>
            {
                if (!_online) return Task.FromResult(false);
                _sent.Add(JsonSerializer.SerializeToElement(payload));
                return Task.FromResult(true);
            });
            return (outbox, spool, handshake);
        }

        private static object Result(int id, string output, int exitCode) => new { type = "result", task_id = id, output, exit_code = exitCode };

        private List<ResultSpool.Entry> OnDisk() => new ResultSpool(SpoolPath).All();

        private static JsonElement Ack(int id) => JsonDocument.Parse($"{{\"action\":\"result_ack\",\"task_id\":{id}}}").RootElement;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Store_WritesToDisk_AndNothingSendsIt(bool ackServer)
        {
            var (outbox, _, _) = Create(ackServer);
            outbox.Store(5, Result(5, "[TAMAM] kapanıyor", 0));
            ResultSpool.Entry stored = Assert.Single(OnDisk());
            Assert.Equal(5, stored.TaskId);
            Assert.Equal("[TAMAM] kapanıyor", stored.Result.GetProperty("output").GetString());

            // Heartbeat de göndermez; başka bir sonuç her zamanki gibi gider
            await outbox.FlushPendingResultsAsync();
            await outbox.SendResultAsync(6, Result(6, "başka", 0));
            await outbox.FlushPendingResultsAsync();
            Assert.Equal(new[] { 6 }, _sent.Select(m => m.GetProperty("task_id").GetInt32()).Distinct());
            Assert.Contains(5, OnDisk().Select(e => e.TaskId));
        }

        [Fact]
        public async Task SendStored_OnAnAckServer_ReplacesSendsAndKeepsUntilTheAck()
        {
            var (outbox, _, _) = Create(true);
            outbox.Store(5, Result(5, "[TAMAM] kapanıyor", 0));
            await outbox.SendStoredAsync(5, Result(5, "[HATA] shutdown.exe 1190: engellendi", 1190));

            JsonElement sent = Assert.Single(_sent);
            Assert.Equal(1190, sent.GetProperty("exit_code").GetInt32());
            ResultSpool.Entry stored = Assert.Single(OnDisk());
            Assert.Equal("[HATA] shutdown.exe 1190: engellendi", stored.Result.GetProperty("output").GetString());
            // Bu bağlantıda gönderildi: heartbeat yeniden göndermez; onayla silinir
            await outbox.FlushPendingResultsAsync();
            Assert.Single(_sent);
            outbox.HandleResultAck(Ack(5));
            Assert.Empty(OnDisk());
        }

        [Fact]
        public async Task SendStored_OnAnOldServer_IsRemovedOnceSent()
        {
            var (outbox, _, _) = Create(false);
            outbox.Store(5, Result(5, "[TAMAM] kapanıyor", 0));
            await outbox.SendStoredAsync(5, Result(5, "[TAMAM] kapanıyor", 0));
            Assert.Equal("[TAMAM] kapanıyor", Assert.Single(_sent).GetProperty("output").GetString());
            Assert.Empty(OnDisk());
            await outbox.FlushPendingResultsAsync();
            Assert.Single(_sent);
        }

        // Gönderilemeyen sonuç diskte kalır: bağlantı gelince heartbeat gönderir (eski sunucuda gönderince siler)
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SendStored_WhileOffline_StaysOnDiskUntilTheConnection(bool ackServer)
        {
            var (outbox, _, _) = Create(ackServer);
            outbox.Store(5, Result(5, "[TAMAM] kapanıyor", 0));
            _online = false;
            await outbox.SendStoredAsync(5, Result(5, "[TAMAM] kapanıyor", 0));
            Assert.Empty(_sent);
            Assert.Single(OnDisk());

            _online = true;
            await outbox.FlushPendingResultsAsync();
            Assert.Equal(5, Assert.Single(_sent).GetProperty("task_id").GetInt32());
            Assert.Equal(ackServer ? 1 : 0, OnDisk().Count);
        }

        // Bağlantının ilk saniyelerinde (server_info henüz gelmedi, önceki bağlantı onaylıydı) gönderilmez; server_info'dan
        // sonraki heartbeat gönderir
        [Fact]
        public async Task SendStored_BeforeServerInfo_WaitsForIt()
        {
            var (outbox, spool, handshake) = Create(true);
            handshake.OnConnected();
            spool.OnConnected();
            outbox.Store(5, Result(5, "[TAMAM] yeniden başlatılıyor", 0));
            await outbox.SendStoredAsync(5, Result(5, "[TAMAM] yeniden başlatılıyor", 0));
            Assert.Empty(_sent);
            Assert.Single(OnDisk());

            handshake.OnServerInfo(JsonDocument.Parse("{\"action\":\"server_info\",\"features\":[\"result_ack\"]}").RootElement);
            await outbox.FlushPendingResultsAsync();
            Assert.Equal(5, Assert.Single(_sent).GetProperty("task_id").GetInt32());
        }

        // Servis saklanan sonucu göndermeden durdu (bilgisayar kapandı): yeni servis diskten okur ve gönderir
        [Fact]
        public async Task Store_ThenServiceRestart_TheResultGoesFromDisk()
        {
            Create(true).Outbox.Store(5, Result(5, "[TAMAM] kapanıyor", 0));
            var (afterBoot, _, _) = Create(true);
            await afterBoot.FlushPendingResultsAsync();
            Assert.Equal("[TAMAM] kapanıyor", Assert.Single(_sent).GetProperty("output").GetString());
        }
    }
}
