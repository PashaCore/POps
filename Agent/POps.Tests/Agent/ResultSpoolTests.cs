using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
            SecureStore.Dir = TestEnvironment.NewDir("spool");
            _path = SecureStore.PathOf(ResultSpool.FileName);
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
}
