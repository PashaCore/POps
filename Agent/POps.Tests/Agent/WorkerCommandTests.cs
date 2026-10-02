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
    // Sunucunun komut mesajları (Worker.HandleServerMessageAsync). Giden mesajlar yakalanır; karantina gerçek güvenlik
    // duvarına dokunmayan sahte yalıtımla, uzaktan komut gerçek cmd.exe ile çalışır.
    public class WorkerCommandTests : TestBase, IDisposable
    {
        private readonly Worker _worker;
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private readonly List<string> _tray = new List<string>();
        private bool _disableSucceeds = true;

        public WorkerCommandTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("worker-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("worker-data");
            AgentCapabilities.Load();   // dosya yok: ikisi de açık
            _worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-TEST",
                SendOverride = payload =>
                {
                    lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(payload));
                    return Task.FromResult(true);
                },
            };
            _worker.Quarantine = new QuarantineControl(
                _tray.Add,
                () => { File.WriteAllText(NetworkIsolation.StatePath, "{}"); return Task.FromResult(true); },
                () =>
                {
                    if (_disableSucceeds) File.Delete(NetworkIsolation.StatePath);
                    return Task.FromResult(_disableSucceeds);
                });
        }

        public void Dispose()
        {
            _worker.Dispose();
            SecureStore.Dir = TestEnvironment.NewDir("worker-reset");
            AgentCapabilities.Load();
        }

        private Task Handle(string json) => _worker.HandleServerMessageAsync(json, null, CancellationToken.None);

        private List<JsonElement> Sent(string type)
        {
            lock (_sent) return _sent.Where(m => m.TryGetProperty("type", out var t) && t.GetString() == type).ToList();
        }

        private async Task<JsonElement> WaitForResult(int taskId)
        {
            for (int i = 0; i < 300; i++)
            {
                JsonElement? hit = Sent("result").Cast<JsonElement?>().FirstOrDefault(m => m.Value.GetProperty("task_id").GetInt32() == taskId);
                if (hit.HasValue) return hit.Value;
                await Task.Delay(100);
            }
            throw new TimeoutException($"Görev {taskId} sonucu gelmedi");
        }

        private static void DisableTerminal()
        {
            File.WriteAllText(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":false,\"vision_enabled\":true}");
            AgentCapabilities.Load();
        }

        // ------------------------------------------------------------------ tanınmayan / bozuk

        [Fact]
        public async Task UnknownAction_IsIgnored()
        {
            await Handle("{\"action\":\"teleport\",\"to\":\"mars\"}");
            await Handle("{\"type\":\"something_else\"}");
            Assert.Empty(_sent);
            Assert.Empty(_tray);
        }

        [Fact]
        public async Task BrokenJson_GoesToTheCaller() =>
            await Assert.ThrowsAnyAsync<JsonException>(() => Handle("{bozuk"));

        // ------------------------------------------------------------------ execute / cancel_task

        [Fact]
        public async Task Execute_WithTerminalDisabled_IsRefusedAndReported()
        {
            DisableTerminal();
            await Handle("{\"action\":\"execute\",\"task_id\":41,\"script_path\":\"whoami\"}");
            JsonElement result = Assert.Single(Sent("result"));
            Assert.Equal(41, result.GetProperty("task_id").GetInt32());
            Assert.Contains("REDDEDİLDİ", result.GetProperty("output").GetString());
            JsonElement denied = Assert.Single(Sent("capability_denied"));
            Assert.Equal(("terminal", "execute", 41), (denied.GetProperty("capability").GetString(), denied.GetProperty("action").GetString(), denied.GetProperty("task_id").GetInt32()));
        }

        [Fact]
        public async Task Execute_RunsAndReportsOutputAndExitCode()
        {
            await Handle("{\"action\":\"execute\",\"task_id\":42,\"script_path\":\"echo merhaba-pops\",\"requested_by\":\"Pasha\"}");
            JsonElement result = await WaitForResult(42);
            Assert.Contains("merhaba-pops", result.GetProperty("output").GetString());
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal("HW-TEST", result.GetProperty("pc_name").GetString());
        }

        [Fact]
        public async Task CancelTask_StopsTheRunningCommand()
        {
            await Handle("{\"action\":\"execute\",\"task_id\":43,\"script_path\":\"ping -n 30 127.0.0.1 > nul\"}");
            for (int i = 0; i < 50 && _worker.CommandRunner.RunningCount == 0; i++) await Task.Delay(100);
            await Handle("{\"action\":\"cancel_task\",\"task_id\":43}");
            JsonElement result = await WaitForResult(43);
            Assert.Equal(CommandRunner.ExitCancelled, result.GetProperty("exit_code").GetInt32());
            Assert.Contains("İPTAL", result.GetProperty("output").GetString());
        }

        [Fact]
        public async Task CancelTask_ForAnUnknownOrMalformedId_DoesNothing()
        {
            await Handle("{\"action\":\"cancel_task\",\"task_id\":999}");
            await Handle("{\"action\":\"cancel_task\",\"task_id\":\"x\"}");
            Assert.Empty(_sent);
        }

        // ------------------------------------------------------------------ yetenekler

        [Fact]
        public async Task SetCapabilities_TurnsVisionOff_AndReportsTheState()
        {
            await Handle("{\"action\":\"set_capabilities\",\"vision_enabled\":false}");
            Assert.False(AgentCapabilities.VisionEnabled);
            JsonElement status = Sent("capabilities").Last();
            Assert.False(status.GetProperty("vision_enabled").GetBoolean());
            Assert.True(status.GetProperty("terminal_enabled").GetBoolean());

            // Sunucu yeniden açamaz
            await Handle("{\"action\":\"set_capabilities\",\"vision_enabled\":true}");
            Assert.False(AgentCapabilities.VisionEnabled);
        }

        [Fact]
        public async Task VisionRequests_WhenVisionIsOff_AreDenied()
        {
            await Handle("{\"action\":\"set_capabilities\",\"vision_enabled\":false}");
            await Handle("{\"action\":\"start_stream\",\"fps\":5}");
            await Handle("{\"action\":\"start_vision_session\",\"session_id\":\"s1\"}");
            Assert.Contains(Sent("capability_denied"), d => d.GetProperty("action").GetString() == "start_stream");
            Assert.Contains(Sent("capability_denied"), d => d.GetProperty("action").GetString() == "start_vision_session");
        }

        [Fact]
        public async Task RemoteInput_ForAnotherDevice_IsIgnored()
        {
            await Handle("{\"type\":\"remote_input\",\"device\":\"HW-OTHER\",\"input_type\":\"keyboard\",\"key\":\"a\"}");
            Assert.Empty(_sent);
        }

        [Fact]
        public async Task RemoteInput_WhenVisionIsOff_IsDenied()
        {
            await Handle("{\"action\":\"set_capabilities\",\"vision_enabled\":false}");
            await Handle("{\"type\":\"remote_input\",\"device\":\"HW-TEST\",\"action\":\"get_thumbnail\"}");
            Assert.Contains(Sent("capability_denied"), d => d.GetProperty("capability").GetString() == "vision");
        }

        [Fact]
        public async Task StopStream_WithoutAStream_IsHarmless()
        {
            await Handle("{\"action\":\"stop_stream\"}");
            Assert.Empty(_sent);
        }

        // ------------------------------------------------------------------ karantina

        [Fact]
        public async Task Lockdown_ThenUnlock()
        {
            await Handle("{\"action\":\"lockdown\",\"reason\":\"Sınav\"}");
            Assert.True(_worker.Quarantine.IsLocked);
            Assert.Equal("Sınav", _worker.Quarantine.LockReason);
            // Tepsiye giden JSON Türkçe karakterleri kaçışlı yazar: içerik ayrıştırılarak denetlenir
            Assert.Contains(_tray, m => m.StartsWith("{") && JsonDocument.Parse(m).RootElement.TryGetProperty("reason", out var r) && r.GetString() == "Sınav");

            await Handle("{\"action\":\"unlock\"}");
            Assert.False(_worker.Quarantine.IsLocked);
        }

        [Fact]
        public async Task Unlock_ThatCannotLiftTheIsolation_KeepsTheLock()
        {
            await Handle("{\"action\":\"lockdown\"}");
            _disableSucceeds = false;
            await Handle("{\"action\":\"unlock\"}");
            Assert.True(_worker.Quarantine.IsLocked);
            Assert.Contains("UNLOCK_FAILED", _tray);
        }

        // ------------------------------------------------------------------ anahtarlar

        [Fact]
        public async Task SetBypassSecret_OverAConnectionWithoutDeviceSecret_IsRefused()
        {
            await Handle("{\"action\":\"set_bypass_secret\",\"secret\":\"" + new string('k', 43) + "\"}");
            Assert.Empty(Sent("bypass_secret_ack"));
        }

        [Fact]
        public async Task SetSecret_Malformed_IsIgnored()
        {
            string before = AgentCredentials.CurrentSecret;
            await Handle("{\"action\":\"set_secret\",\"secret\":\"kısa\"}");
            await Handle("{\"action\":\"set_secret\",\"secret\":5}");
            Assert.Equal(before, AgentCredentials.CurrentSecret);
        }

        // ------------------------------------------------------------------ güncelleme sonucu onayı

        private void WriteResult() =>
            File.WriteAllText(AgentUpdate.ResultPath, "{\"outcome\":\"success\",\"from_version\":\"0.1.13-alpha\",\"to_version\":\"0.1.14-alpha\"}");

        [Fact]
        public async Task UpdateResult_AckServer_KeepsTheFileUntilTheMatchingAck()
        {
            WriteResult();
            _worker.UpdateResults.OnConnected();
            await _worker.ReportUpdateResultAsync(CancellationToken.None);
            Assert.Empty(Sent("update_result"));   // server_info bekleniyor

            await Handle("{\"action\":\"server_info\",\"version\":\"0.1.14\",\"features\":[\"update_result_ack\"]}");
            await _worker.ReportUpdateResultAsync(CancellationToken.None);
            JsonElement sent = Assert.Single(Sent("update_result"));
            string id = sent.GetProperty("result_id").GetString();
            Assert.Equal(AgentUpdate.PendingResultId(), id);
            Assert.True(File.Exists(AgentUpdate.ResultPath));

            // Hemen yeniden gönderilmez
            await _worker.ReportUpdateResultAsync(CancellationToken.None);
            Assert.Single(Sent("update_result"));

            await Handle("{\"action\":\"update_result_ack\",\"result_id\":\"ffffffffffffffffffffffffffffffff\"}");
            Assert.True(File.Exists(AgentUpdate.ResultPath));
            await Handle("{\"action\":\"update_result_ack\",\"result_id\":\"" + id + "\"}");
            Assert.False(File.Exists(AgentUpdate.ResultPath));
            Assert.True(File.Exists(AgentUpdate.ReportedResultPath));
        }

        [Fact]
        public async Task UpdateResult_OldServer_SentAndSetAsideAfter15Seconds()
        {
            WriteResult();
            DateTime now = DateTime.UtcNow;
            _worker.UpdateResults.UtcNow = () => now;
            _worker.UpdateResults.OnConnected();
            await _worker.ReportUpdateResultAsync(CancellationToken.None);
            Assert.Empty(Sent("update_result"));
            now += UpdateResultReporter.ServerInfoWait;
            await _worker.ReportUpdateResultAsync(CancellationToken.None);
            Assert.Single(Sent("update_result"));
            Assert.False(File.Exists(AgentUpdate.ResultPath));
        }

        [Fact]
        public async Task UpdateResult_NotSent_StaysForTheNextTry()
        {
            WriteResult();
            _worker.SendOverride = _ => Task.FromResult(false);
            DateTime now = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            _worker.UpdateResults.UtcNow = () => now;
            await _worker.ReportUpdateResultAsync(CancellationToken.None);
            Assert.True(File.Exists(AgentUpdate.ResultPath));
        }
    }

    // Ağ yalıtımı: aç / kapat / sunucu adresini yenile, sahte PowerShell çalıştırıcısıyla
    public class NetworkIsolationFlowTests : TestBase, IDisposable
    {
        private readonly List<string> _scripts = new List<string>();
        private int _exit;
        private const string Profiles = "[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"False\"},{\"Name\":\"Public\",\"Enabled\":\"True\"}]";

        private readonly Func<string, Task<(int Exit, string Output)>> _realRunner = NetworkIsolation.ScriptRunner;

        public NetworkIsolationFlowTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("iso-flow");
            NetworkIsolation.ScriptRunner = script =>
            {
                _scripts.Add(script);
                return Task.FromResult((_exit, _exit == 0 ? "uyarı satırı\n" + Profiles : "erişim reddedildi"));
            };
        }

        public void Dispose() => NetworkIsolation.ScriptRunner = _realRunner;

        private static List<string> Servers() => NetworkIsolation.ReadServerAddresses(File.ReadAllText(NetworkIsolation.StatePath));

        [Fact]
        public async Task Enable_RecordsProfilesAndServer()
        {
            Assert.True(await NetworkIsolation.EnableAsync("https://203.0.113.10"));
            Assert.Single(_scripts);
            Assert.Contains("-Action Block", _scripts[0]);
            Assert.True(NetworkIsolation.IsActive);
            Assert.Equal(new[] { "Private" }, NetworkIsolation.ReadPreviouslyDisabledProfiles());
            Assert.Equal(new[] { "203.0.113.10" }, Servers());
        }

        [Fact]
        public async Task Enable_WithoutAServerAddress_DoesNothing()
        {
            Assert.False(await NetworkIsolation.EnableAsync("bu bir adres değil"));
            Assert.Empty(_scripts);
            Assert.False(NetworkIsolation.IsActive);
        }

        [Fact]
        public async Task Enable_ScriptFailure_LeavesNoState()
        {
            _exit = 1;
            Assert.False(await NetworkIsolation.EnableAsync("https://203.0.113.10"));
            Assert.False(NetworkIsolation.IsActive);
        }

        [Fact]
        public async Task Refresh_WhenNotIsolated_DoesNothing()
        {
            Assert.Equal(NetworkIsolation.RefreshResult.NotIsolated, await NetworkIsolation.RefreshServerAddressesAsync("https://203.0.113.10", "test"));
            Assert.Empty(_scripts);
        }

        [Fact]
        public async Task Refresh_SameAddress_RebuildsNothing()
        {
            await NetworkIsolation.EnableAsync("https://203.0.113.10");
            Assert.Equal(NetworkIsolation.RefreshResult.Unchanged, await NetworkIsolation.RefreshServerAddressesAsync("https://203.0.113.10", "test"));
            Assert.Single(_scripts);
        }

        [Fact]
        public async Task Refresh_NewAddress_RebuildsRules_KeepsTheFirstProfiles()
        {
            await NetworkIsolation.EnableAsync("https://203.0.113.10");
            // Yenilemede betik "şimdiki" profilleri döndürür (hepsi açık): kayıt yine de ilk hâlini korumalı
            NetworkIsolation.ScriptRunner = script =>
            {
                _scripts.Add(script);
                return Task.FromResult((0, "[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"True\"},{\"Name\":\"Public\",\"Enabled\":\"True\"}]"));
            };
            Assert.Equal(NetworkIsolation.RefreshResult.Updated, await NetworkIsolation.RefreshServerAddressesAsync("https://198.51.100.7", "test"));
            Assert.Equal(2, _scripts.Count);
            Assert.DoesNotContain("'198.51.100.7'", _scripts[1]);   // yeni adres engellenen aralıkların dışında
            Assert.Equal(new[] { "198.51.100.7" }, Servers());
            Assert.Equal(new[] { "Private" }, NetworkIsolation.ReadPreviouslyDisabledProfiles());
        }

        [Fact]
        public async Task Refresh_StateFromAnOlderAgent_RebuildsOnce()
        {
            File.WriteAllText(NetworkIsolation.StatePath, "{\"previous_profiles\":" + Profiles + ",\"since\":1}");
            Assert.Equal(NetworkIsolation.RefreshResult.Updated, await NetworkIsolation.RefreshServerAddressesAsync("https://203.0.113.10", "test"));
            Assert.Equal(NetworkIsolation.RefreshResult.Unchanged, await NetworkIsolation.RefreshServerAddressesAsync("https://203.0.113.10", "test"));
            Assert.Single(_scripts);
        }

        [Fact]
        public async Task Refresh_ResolveOrScriptFailure_LeavesTheRulesAlone()
        {
            await NetworkIsolation.EnableAsync("https://203.0.113.10");
            string before = File.ReadAllText(NetworkIsolation.StatePath);
            Assert.Equal(NetworkIsolation.RefreshResult.ResolveFailed, await NetworkIsolation.RefreshServerAddressesAsync("adres yok", "test"));
            _exit = 1;
            Assert.Equal(NetworkIsolation.RefreshResult.Failed, await NetworkIsolation.RefreshServerAddressesAsync("https://198.51.100.7", "test"));
            Assert.Equal(before, File.ReadAllText(NetworkIsolation.StatePath));
        }

        [Fact]
        public async Task Disable_RestoresTheProfilesThatWereOff()
        {
            await NetworkIsolation.EnableAsync("https://203.0.113.10");
            Assert.True(await NetworkIsolation.DisableAsync());
            Assert.Contains("Set-NetFirewallProfile -Profile Private -Enabled False", _scripts.Last());
            Assert.DoesNotContain("-Profile Domain -Enabled False", _scripts.Last());
            Assert.False(NetworkIsolation.IsActive);
        }

        [Fact]
        public async Task Disable_Failure_KeepsTheState()
        {
            await NetworkIsolation.EnableAsync("https://203.0.113.10");
            _exit = 1;
            Assert.False(await NetworkIsolation.DisableAsync());
            Assert.True(NetworkIsolation.IsActive);
        }
    }
}
