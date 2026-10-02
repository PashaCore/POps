using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // A3: karantinada sunucu adresi değişirse izin listesi yenilenir; önceki profil durumu korunur
    public class IsolationRefreshTests : TestBase
    {
        private static IPAddress Ip(string s) => IPAddress.Parse(s);

        [Fact]
        public void SameAddresses_IgnoresOrderDuplicatesMappingAndScope()
        {
            Assert.True(NetworkIsolation.SameAddresses(new[] { Ip("203.0.113.10"), Ip("2001:db8::1") }, new[] { "2001:db8::1", "203.0.113.10" }));
            Assert.True(NetworkIsolation.SameAddresses(new[] { Ip("::ffff:203.0.113.10"), Ip("203.0.113.10") }, new[] { "203.0.113.10" }));
            Assert.True(NetworkIsolation.SameAddresses(new[] { Ip("fe80::1%12") }, new[] { "fe80::1" }));
            Assert.False(NetworkIsolation.SameAddresses(new[] { Ip("203.0.113.11") }, new[] { "203.0.113.10" }));
            Assert.False(NetworkIsolation.SameAddresses(new[] { Ip("203.0.113.10"), Ip("203.0.113.11") }, new[] { "203.0.113.10" }));
            Assert.False(NetworkIsolation.SameAddresses(new[] { Ip("203.0.113.10") }, new[] { "çöp" }));
        }

        [Fact]
        public void Merge_KeepsPreviousProfilesAndSince_ReplacesOnlyServerAddresses()
        {
            const string first = "{\"previous_profiles\":[{\"Name\":\"Domain\",\"Enabled\":\"True\"},{\"Name\":\"Private\",\"Enabled\":\"False\"}],\"since\":1700000000}";
            string withServer = NetworkIsolation.MergeServerAddresses(first, new[] { Ip("203.0.113.10") });
            string refreshed = NetworkIsolation.MergeServerAddresses(withServer, new[] { Ip("198.51.100.7"), Ip("198.51.100.7") });

            using JsonDocument doc = JsonDocument.Parse(refreshed);
            Assert.Equal(1700000000, doc.RootElement.GetProperty("since").GetInt64());
            Assert.Equal("False", doc.RootElement.GetProperty("previous_profiles")[1].GetProperty("Enabled").GetString());
            Assert.Equal(new[] { "198.51.100.7" }, NetworkIsolation.ReadServerAddresses(refreshed));
        }

        [Fact]
        public void Refresh_DoesNotTouchTheProfilesThatUnlockRestores()
        {
            SecureStore.Dir = TestEnvironment.NewDir("iso-refresh");
            File.WriteAllText(NetworkIsolation.StatePath, "{\"previous_profiles\":[{\"Name\":\"Public\",\"Enabled\":\"False\"}],\"since\":5}");
            string merged = NetworkIsolation.MergeServerAddresses(File.ReadAllText(NetworkIsolation.StatePath), new[] { Ip("203.0.113.99") });
            File.WriteAllText(NetworkIsolation.StatePath, merged);
            Assert.Equal(new[] { "Public" }, NetworkIsolation.ReadPreviouslyDisabledProfiles());
            Assert.True(NetworkIsolation.IsActive);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{\"previous_profiles\":[]}")]
        [InlineData("{bozuk")]
        [InlineData("{\"server_addresses\":\"203.0.113.1\"}")]
        public void UnknownServerAddresses_AreNull(string state) => Assert.Null(NetworkIsolation.ReadServerAddresses(state));

        [Fact]
        public void Merge_OfAnUnreadableState_StillRecordsTheServer() =>
            Assert.Equal(new[] { "203.0.113.10" }, NetworkIsolation.ReadServerAddresses(NetworkIsolation.MergeServerAddresses("{bozuk", new[] { Ip("203.0.113.10") })));

        [Fact]
        public void NormalizeAddresses_SortedAndDistinct() =>
            Assert.Equal(new[] { "10.0.0.2", "10.0.0.9", "2001:db8::5" },
                NetworkIsolation.NormalizeAddresses(new[] { Ip("2001:db8::5"), Ip("10.0.0.9"), Ip("::ffff:10.0.0.2"), Ip("10.0.0.9"), null }));

        [Fact]
        public void EnableScript_AddsNewRulesBeforeRemovingOldOnes()
        {
            string script = NetworkIsolation.BuildEnableScript(NetworkIsolation.AllowedRanges(new[] { Ip("203.0.113.10") }));
            int capture = script.IndexOf("$old = @(Get-NetFirewallRule -Group $group", StringComparison.Ordinal);
            int add = script.IndexOf("New-NetFirewallRule", StringComparison.Ordinal);
            int remove = script.IndexOf("$old | Remove-NetFirewallRule", StringComparison.Ordinal);
            Assert.True(capture >= 0 && capture < add && add < remove, script);
        }

        [Fact]
        public void RefreshEvent_RecordsOldAndNewAddresses()
        {
            LocalAuditEvent e = LocalAudit.QuarantineAllowListRefreshed(new[] { "203.0.113.10" }, new[] { "198.51.100.7" }, "periyodik denetim");
            Assert.Equal(1022, e.EventId);
            Assert.Contains("old: 203.0.113.10", e.Message);
            Assert.Contains("new: 198.51.100.7", e.Message);
            Assert.Contains("(bilinmiyor)", LocalAudit.QuarantineAllowListRefreshed(null, new[] { "1.2.3.4" }, "x").Message);
        }
    }

    // A4: açılışta yarım kalmış görev dosyaları silinir; başka dosyalara dokunulmaz
    public class StaleTaskFileTests : TestBase
    {
        private const string Hex = "0123456789abcdef0123456789abcdef";

        [Theory]
        [InlineData("pops_task_" + Hex + ".bat", true)]
        [InlineData("pops_task_" + Hex + ".BAT", false)]
        [InlineData("pops_task_0123456789ABCDEF0123456789ABCDEF.bat", false)]
        [InlineData("pops_task_0123.bat", false)]
        [InlineData("pops_task_" + Hex + ".bat.txt", false)]
        [InlineData("x_pops_task_" + Hex + ".bat", false)]
        [InlineData("pops_task_" + Hex + "0.bat", false)]
        [InlineData(null, false)]
        public void Pattern(string name, bool match) => Assert.Equal(match, CommandRunner.IsTaskFile(name));

        [Fact]
        public void Cleanup_DeletesOnlyTaskFiles()
        {
            string dir = TestEnvironment.NewDir("stale-bat");
            string a = Path.Combine(dir, "pops_task_" + Hex + ".bat");
            string b = Path.Combine(dir, "pops_task_" + Hex.Replace('0', 'f') + ".bat");
            string keep1 = Path.Combine(dir, "pops_task_notes.bat");
            string keep2 = Path.Combine(dir, "other.bat");
            string keep3 = Path.Combine(dir, "pops_task_" + Hex + ".bat.bak");
            foreach (string f in new[] { a, b, keep1, keep2, keep3 }) File.WriteAllText(f, "@echo off");

            var (deleted, failed) = CommandRunner.CleanupStaleTaskFiles(dir);
            Assert.Equal((2, 0), (deleted, failed));
            Assert.False(File.Exists(a));
            Assert.False(File.Exists(b));
            Assert.True(File.Exists(keep1) && File.Exists(keep2) && File.Exists(keep3));
        }

        [Fact]
        public void Cleanup_ReportsAFileThatCannotBeDeleted_AndGoesOn()
        {
            string dir = TestEnvironment.NewDir("stale-bat-locked");
            string locked = Path.Combine(dir, "pops_task_" + Hex + ".bat");
            string free = Path.Combine(dir, "pops_task_" + Hex.Replace('1', 'e') + ".bat");
            File.WriteAllText(locked, "x");
            File.WriteAllText(free, "x");
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var (deleted, failed) = CommandRunner.CleanupStaleTaskFiles(dir);
                Assert.Equal((1, 1), (deleted, failed));
            }
            Assert.True(File.Exists(locked));
        }

        [Fact]
        public void Cleanup_OfAMissingDirectory_DoesNotThrow() =>
            Assert.Equal((0, 0), CommandRunner.CleanupStaleTaskFiles(Path.Combine(TestEnvironment.Root, "yok-" + Guid.NewGuid().ToString("N"))));
    }

    // wake_peer: sihirli paket yapısı ve MAC doğrulaması (gönderim ağa çıkmadan sınanır)
    public class WakeOnLanTests : TestBase
    {
        [Theory]
        [InlineData("00:1A:2b:3C:4d:5E")]
        [InlineData("00-1A-2B-3C-4D-5E")]
        [InlineData("001A.2B3C.4D5E")]
        [InlineData("001A2B3C4D5E")]
        [InlineData(" 00:1A:2B:3C:4D:5E ")]
        public void MagicPacket_SixFFsThenTheMacSixteenTimes(string mac)
        {
            byte[] packet = WakeOnLan.BuildMagicPacket(mac);
            Assert.Equal(102, packet.Length);
            Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
            byte[] expected = { 0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E };
            for (int i = 1; i <= 16; i++) Assert.Equal(expected, packet.Skip(i * 6).Take(6).ToArray());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("00:1A:2B:3C:4D")]
        [InlineData("00:1A:2B:3C:4D:5E:6F")]
        [InlineData("zz:1A:2B:3C:4D:5E")]
        [InlineData("+0:1A:2B:3C:4D:5E")]
        public void InvalidMac_NoPacket(string mac) => Assert.Null(WakeOnLan.BuildMagicPacket(mac));
    }

    // A5: update_result sunucu onaylayana kadar saklanır
    public class UpdateResultAckTests : TestBase
    {
        private DateTime _now = new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);

        private UpdateResultReporter NewReporter()
        {
            var r = new UpdateResultReporter { UtcNow = () => _now };
            r.OnConnected();
            return r;
        }

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        private static readonly JsonElement AckServer = Json("{\"action\":\"server_info\",\"version\":\"0.1.14\",\"features\":[\"update_result_ack\"]}");

        [Fact]
        public void OldServer_WaitsFor15Seconds_ThenOldBehaviour()
        {
            UpdateResultReporter r = NewReporter();
            Assert.Equal(UpdateResultReporter.Step.Wait, r.Next("abc"));
            _now += TimeSpan.FromSeconds(14);
            Assert.Equal(UpdateResultReporter.Step.Wait, r.Next("abc"));
            _now += TimeSpan.FromSeconds(1);
            Assert.Equal(UpdateResultReporter.Step.SendAndMarkReported, r.Next("abc"));
        }

        [Fact]
        public void ServerInfoWithoutTheFeature_IsAnOldServer()
        {
            UpdateResultReporter r = NewReporter();
            r.OnServerInfo(Json("{\"action\":\"server_info\",\"version\":\"0.1.13\",\"features\":[\"other\"]}"));
            Assert.False(r.AckSupported);
            Assert.Equal(UpdateResultReporter.Step.SendAndMarkReported, r.Next("abc"));
        }

        [Fact]
        public void AckServer_KeepsTheResult_ResendsEvery60Seconds()
        {
            UpdateResultReporter r = NewReporter();
            r.OnServerInfo(AckServer);
            Assert.True(r.AckSupported);
            Assert.Equal(UpdateResultReporter.Step.SendAndKeep, r.Next("abc"));
            r.Sent("abc");
            _now += TimeSpan.FromSeconds(59);
            Assert.Equal(UpdateResultReporter.Step.Nothing, r.Next("abc"));
            _now += TimeSpan.FromSeconds(1);
            Assert.Equal(UpdateResultReporter.Step.SendAndKeep, r.Next("abc"));
            // Yeni bir sonuç beklemeden gönderilir
            Assert.Equal(UpdateResultReporter.Step.SendAndKeep, r.Next("def"));
        }

        [Fact]
        public void Reconnect_ResetsTheFlag()
        {
            UpdateResultReporter r = NewReporter();
            r.OnServerInfo(AckServer);
            r.OnConnected();
            Assert.False(r.AckSupported);
            Assert.Equal(UpdateResultReporter.Step.Wait, r.Next("abc"));
        }

        [Fact]
        public void NoResult_NothingToDo() => Assert.Equal(UpdateResultReporter.Step.Nothing, NewReporter().Next(null));

        [Theory]
        [InlineData("{\"action\":\"update_result_ack\",\"result_id\":\"abc\"}", "abc", true)]
        [InlineData("{\"action\":\"update_result_ack\",\"result_id\":\"xyz\"}", "abc", false)]
        [InlineData("{\"action\":\"update_result_ack\"}", "abc", false)]
        [InlineData("{\"action\":\"update_result_ack\",\"result_id\":1}", "abc", false)]
        [InlineData("{\"action\":\"update_result_ack\",\"result_id\":\"abc\"}", null, false)]
        [InlineData("[]", "abc", false)]
        public void Ack_MatchesOnlyThePendingResult(string ack, string pending, bool match) =>
            Assert.Equal(match, UpdateResultReporter.Acknowledges(Json(ack), pending));

        [Fact]
        public void ResultId_IsSha256OfTheRawFile_First32LowercaseHex()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("result-id");
            Assert.Null(AgentUpdate.PendingResultId());
            byte[] raw = Encoding.UTF8.GetBytes("{\"outcome\":\"success\",\"to_version\":\"0.1.14-alpha\"}");
            File.WriteAllBytes(AgentUpdate.ResultPath, raw);

            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant().Substring(0, 32);
            Assert.Equal(expected, AgentUpdate.PendingResultId());
            Assert.Equal(expected, AgentUpdate.PendingResultMessage()["result_id"]);
            Assert.Equal(32, expected.Length);
            // Aynı sonuç için hep aynı değer
            Assert.Equal(AgentUpdate.PendingResultId(), AgentUpdate.PendingResultId());
        }

        [Fact]
        public void ResultFile_StaysUntilAMatchingAck()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("result-ack");
            File.WriteAllText(AgentUpdate.ResultPath, "{\"outcome\":\"success\"}");
            UpdateResultReporter r = NewReporter();
            r.OnServerInfo(AckServer);
            string id = (string)AgentUpdate.PendingResultMessage()["result_id"];
            Assert.Equal(UpdateResultReporter.Step.SendAndKeep, r.Next(id));
            r.Sent(id);

            // Yanlış kimlik: dosya kalır
            Assert.False(UpdateResultReporter.Acknowledges(Json("{\"action\":\"update_result_ack\",\"result_id\":\"0000\"}"), AgentUpdate.PendingResultId()));
            Assert.True(File.Exists(AgentUpdate.ResultPath));

            // Doğru kimlik: kenara alınır
            Assert.True(UpdateResultReporter.Acknowledges(Json("{\"action\":\"update_result_ack\",\"result_id\":\"" + id + "\"}"), AgentUpdate.PendingResultId()));
            AgentUpdate.MarkResultReported();
            Assert.False(File.Exists(AgentUpdate.ResultPath));
            Assert.Null(AgentUpdate.PendingResultId());
        }

        [Fact]
        public void ResultWithBom_IsStillRead()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("result-bom");
            File.WriteAllText(AgentUpdate.ResultPath, "{\"outcome\":\"success\"}", new UTF8Encoding(true));
            Assert.Equal("success", AgentUpdate.PendingResultMessage()["status"]);
        }
    }
}
