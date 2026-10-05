using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // B1: çıktı satır sonu beklemeden sabit parçalarla okunur; B3: aynı görev kimliği iki kez çalışmaz
    public class CommandOutputChunkTests : TestBase
    {
        [Fact]
        public void Append_StopsAtTheLimit_AndCountsTheRest()
        {
            var output = new BoundedOutput(10);
            output.Append("12345678".ToCharArray(), 0, 8);
            output.Append("xxabcdefxx".ToCharArray(), 2, 6);
            Assert.Equal(4, output.DroppedChars);
            Assert.StartsWith("12345678ab", output.ToString());
            Assert.Contains("[ÇIKTI KISALTILDI: 4 karakter atıldı]", output.ToString());

            output.Append(null, 0, 5);
            output.Append("abc".ToCharArray(), 0, 0);
            Assert.Equal(4, output.DroppedChars);
        }

        [Fact]
        public void Append_And_AppendLine_ShareTheLimit()
        {
            var output = new BoundedOutput(6);
            output.AppendLine("ab");
            output.Append("cdefgh".ToCharArray(), 0, 6);
            Assert.StartsWith("ab\ncde", output.ToString());
            Assert.Equal(3, output.DroppedChars);
        }

        // 20 milyon karakter, hiç satır sonu yok: eskiden .NET'in satır okuyucusunda bütünüyle birikiyordu
        [Fact]
        public async Task HugeOutputWithoutNewlines_IsBoundedAndTheProcessFinishes()
        {
            CommandExecutionResult result = await new CommandRunner()
                .RunAsync(11, "powershell -NoProfile -Command \"[Console]::Out.Write('x' * 20000000)\"", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Output.Length <= CommandExecutionPolicy.MaxOutputChars, result.Output.Length.ToString());
            Assert.Contains("KISALTILDI", result.Output);
            Assert.StartsWith("xxxx", result.Output);
        }

        [Fact]
        public async Task LineEndings_AreSentAsNewlines()
        {
            CommandExecutionResult result = await new CommandRunner().RunAsync(12, "echo bir\r\necho iki", CancellationToken.None);
            Assert.Equal("bir\niki", result.Output);
        }

        [Fact]
        public async Task StandardError_IsReadInChunksToo()
        {
            CommandExecutionResult result = await new CommandRunner().RunAsync(13, "echo hata-metni 1>&2\r\nexit /b 4", CancellationToken.None);
            Assert.Equal(4, result.ExitCode);
            Assert.Contains("[HATA]:\nhata-metni", result.Output);
        }

        [Fact]
        public async Task SameTaskId_DoesNotStartASecondProcess()
        {
            var runner = new CommandRunner();
            Task<CommandExecutionResult> first = runner.RunAsync(21, "ping -n 30 127.0.0.1 > nul", CancellationToken.None);
            Assert.True(runner.IsRunning(21));

            CommandExecutionResult second = await runner.RunAsync(21, "echo ikinci", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CommandRunner.ExitDuplicate, second.ExitCode);
            Assert.Equal(1, runner.RunningCount);

            Assert.True(runner.Cancel(21));
            Assert.Equal(CommandRunner.ExitCancelled, (await first.WaitAsync(TimeSpan.FromSeconds(20))).ExitCode);
            Assert.False(runner.IsRunning(21));

            // Bittikten sonra aynı kimlik yeniden çalışabilir
            Assert.Equal(0, (await runner.RunAsync(21, "echo yeniden", CancellationToken.None)).ExitCode);
        }

        [Fact]
        public void ExitCodes_AreDistinct()
        {
            int[] codes = { CommandRunner.ExitTimeout, CommandRunner.ExitCancelled, CommandRunner.ExitAgentError, CommandRunner.ExitServiceStopping, CommandRunner.ExitDenied, CommandRunner.ExitDuplicate };
            Assert.Equal(codes.Length, codes.Distinct().Count());
            Assert.Equal((-5, -6), (CommandRunner.ExitDenied, CommandRunner.ExitDuplicate));
        }
    }

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

    // B2-B4 Worker üzerinden: ret çıkış kodu, yinelenen görev, onaylı/onaysız sunucu, yeniden bağlanma, yeniden başlama
    public class WorkerResultAckTests : TestBase, IDisposable
    {
        private Worker _worker;
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private bool _sendSucceeds = true;
        private DateTime _now = DateTime.UtcNow;

        private static readonly string AckServerInfo = "{\"action\":\"server_info\",\"version\":\"0.1.14\",\"features\":[\"update_result_ack\",\"result_ack\"]}";

        public WorkerResultAckTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("worker-ack");
            AgentUpdate.DataDir = TestEnvironment.NewDir("worker-ack-data");
            AgentCapabilities.Load();
            _worker = NewWorker();
        }

        private Worker NewWorker()
        {
            var worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-TEST",
                SendOverride = payload =>
                {
                    if (!_sendSucceeds) return Task.FromResult(false);
                    lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(payload));
                    return Task.FromResult(true);
                },
            };
            worker.Handshake.UtcNow = () => _now;
            return worker;
        }

        public void Dispose()
        {
            _worker.Dispose();
            SecureStore.Dir = TestEnvironment.NewDir("worker-ack-reset");
            AgentCapabilities.Load();
        }

        private Task Handle(string json) => _worker.HandleServerMessageAsync(json, null, CancellationToken.None);

        private string SpoolPath => SecureStore.PathOf(ResultSpool.FileName);

        private List<JsonElement> Sent(string type) { lock (_sent) return _sent.Where(m => m.TryGetProperty("type", out var t) && t.GetString() == type).ToList(); }

        private static void DisableTerminal()
        {
            File.WriteAllText(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":false,\"vision_enabled\":true}");
            AgentCapabilities.Load();
        }

        // Terminal kapalı: belirleyici, süreçsiz bir sonuç üretir
        private Task DeniedExecute(int taskId) => Handle("{\"action\":\"execute\",\"task_id\":" + taskId + ",\"script_path\":\"whoami\"}");

        // ------------------------------------------------------------------ B2

        [Fact]
        public async Task Denied_ResultHasExitCodeMinus5_ThenCapabilityDenied()
        {
            DisableTerminal();
            await DeniedExecute(51);
            List<JsonElement> all;
            lock (_sent) all = _sent.ToList();
            Assert.Equal(2, all.Count);
            Assert.Equal("result", all[0].GetProperty("type").GetString());
            Assert.Equal(CommandRunner.ExitDenied, all[0].GetProperty("exit_code").GetInt32());
            Assert.Equal(51, all[0].GetProperty("task_id").GetInt32());
            Assert.Equal("capability_denied", all[1].GetProperty("type").GetString());
        }

        // ------------------------------------------------------------------ B3

        [Fact]
        public async Task DuplicateExecute_IsIgnored_OneResult()
        {
            await Handle("{\"action\":\"execute\",\"task_id\":52,\"script_path\":\"ping -n 30 127.0.0.1 > nul\"}");
            await Handle("{\"action\":\"execute\",\"task_id\":52,\"script_path\":\"echo ikinci\"}");
            Assert.Equal(1, _worker.CommandRunner.RunningCount);
            await Handle("{\"action\":\"cancel_task\",\"task_id\":52}");
            for (int i = 0; i < 200 && Sent("result").Count == 0; i++) await Task.Delay(100);
            await Task.Delay(500);
            JsonElement result = Assert.Single(Sent("result"));
            Assert.Equal(CommandRunner.ExitCancelled, result.GetProperty("exit_code").GetInt32());
        }

        // ------------------------------------------------------------------ B4

        [Fact]
        public async Task AckServer_ResultStaysOnDiskUntilResultAck()
        {
            DisableTerminal();
            _worker.OnCommandSocketOpened();
            await Handle(AckServerInfo);
            await DeniedExecute(61);

            Assert.Single(Sent("result"));
            Assert.True(File.Exists(SpoolPath));
            Assert.True(_worker.Results.Contains(61));

            // Aynı bağlantıda yeniden gönderilmez
            await _worker.FlushPendingResultsAsync();
            Assert.Single(Sent("result"));

            await Handle("{\"action\":\"result_ack\",\"task_id\":99}");
            Assert.True(_worker.Results.Contains(61));
            await Handle("{\"action\":\"result_ack\",\"task_id\":61}");
            Assert.False(_worker.Results.Contains(61));
            Assert.False(File.Exists(SpoolPath));
        }

        [Fact]
        public async Task Reconnect_ResendsUnacknowledgedResults()
        {
            DisableTerminal();
            _worker.OnCommandSocketOpened();
            await Handle(AckServerInfo);
            await DeniedExecute(62);
            Assert.Single(Sent("result"));

            _worker.OnCommandSocketOpened();
            await _worker.FlushPendingResultsAsync();
            Assert.Single(Sent("result"));   // server_info bekleniyor
            await Handle(AckServerInfo);
            await _worker.FlushPendingResultsAsync();
            Assert.Equal(2, Sent("result").Count(r => r.GetProperty("task_id").GetInt32() == 62));
            Assert.True(_worker.Results.Contains(62));
        }

        [Fact]
        public async Task OldServer_OldBehaviour_NoSpoolFile()
        {
            DisableTerminal();
            _worker.OnCommandSocketOpened();
            _now += ServerHandshake.ServerInfoWait;
            await DeniedExecute(63);
            Assert.Single(Sent("result"));
            Assert.False(File.Exists(SpoolPath));
            Assert.Equal(0, _worker.Results.Count);
        }

        [Fact]
        public async Task OldServer_ResultThatCouldNotBeSent_WaitsInMemory()
        {
            DisableTerminal();
            _worker.OnCommandSocketOpened();
            _now += ServerHandshake.ServerInfoWait;
            _sendSucceeds = false;
            await DeniedExecute(64);
            Assert.False(File.Exists(SpoolPath));
            _sendSucceeds = true;
            await _worker.FlushPendingResultsAsync();
            Assert.Single(Sent("result"));
        }

        [Fact]
        public async Task AckServer_SendFailure_StaysAndIsSentOnTheNextFlush()
        {
            DisableTerminal();
            _worker.OnCommandSocketOpened();
            await Handle(AckServerInfo);
            _sendSucceeds = false;
            await DeniedExecute(65);
            Assert.True(_worker.Results.Contains(65));
            _sendSucceeds = true;
            await _worker.FlushPendingResultsAsync();
            Assert.Single(Sent("result"));
        }

        // Bağlantının ilk saniyelerinde (server_info gelmeden) üretilen sonuç, önceki bağlantı onaylıysa diske yazılır
        [Fact]
        public async Task ResultBeforeServerInfo_AfterAnAckConnection_IsKeptOnDisk()
        {
            DisableTerminal();
            _worker.OnCommandSocketOpened();
            await Handle(AckServerInfo);
            _worker.OnCommandSocketOpened();
            _sendSucceeds = false;
            await DeniedExecute(66);
            _sendSucceeds = true;
            Assert.True(_worker.Results.Contains(66));
            await Handle(AckServerInfo);
            await _worker.FlushPendingResultsAsync();
            Assert.Contains(Sent("result"), r => r.GetProperty("task_id").GetInt32() == 66);
        }

        [Fact]
        public async Task ServiceRestart_SpoolIsReadAndResent()
        {
            new ResultSpool(SpoolPath).Add(70, new { type = "result", pc_name = "HW-TEST", task_id = 70, output = "önceki çalışma", exit_code = 0 });
            _worker.Dispose();
            _worker = NewWorker();
            Assert.True(_worker.Results.Contains(70));

            _worker.OnCommandSocketOpened();
            await Handle(AckServerInfo);
            await _worker.FlushPendingResultsAsync();
            JsonElement resent = Assert.Single(Sent("result"));
            Assert.Equal("önceki çalışma", resent.GetProperty("output").GetString());
            Assert.True(File.Exists(SpoolPath));
            await Handle("{\"action\":\"result_ack\",\"task_id\":70}");
            Assert.False(File.Exists(SpoolPath));
        }

        // Diskte kalmış sonuç, onay vermeyen bir sunucuya gönderilince silinir (sonsuza dek kalmaz)
        [Fact]
        public async Task OldServer_DrainsTheSpool()
        {
            new ResultSpool(SpoolPath).Add(71, new { type = "result", task_id = 71, output = "x", exit_code = 0 });
            _worker.Dispose();
            _worker = NewWorker();
            _worker.OnCommandSocketOpened();
            await Handle("{\"action\":\"server_info\",\"version\":\"0.1.13\",\"features\":[\"update_result_ack\"]}");
            await _worker.FlushPendingResultsAsync();
            Assert.Single(Sent("result"));
            Assert.False(File.Exists(SpoolPath));
        }

        [Theory]
        [InlineData("{\"action\":\"result_ack\"}")]
        [InlineData("{\"action\":\"result_ack\",\"task_id\":\"61\"}")]
        [InlineData("{\"action\":\"result_ack\",\"task_id\":1.5}")]
        public async Task MalformedResultAck_IsIgnored(string ack)
        {
            DisableTerminal();
            _worker.OnCommandSocketOpened();
            await Handle(AckServerInfo);
            await DeniedExecute(61);
            await Handle(ack);
            Assert.True(_worker.Results.Contains(61));
        }
    }
}
