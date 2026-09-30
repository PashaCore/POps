using System;
using System.Collections.Generic;
using System.Linq;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class LocalAuditTests : TestBase
    {
        [Fact]
        public void CommandEvents_ContainHashAndMetadata_ButNeverCommandText()
        {
            const string command = "net use \\\\server /user:admin secret-password";
            LocalAuditEvent started = LocalAudit.CommandStarted(42, command, "Pasha\r\nforged");
            LocalAuditEvent finished = LocalAudit.CommandFinished(42, 5, TimeSpan.FromMilliseconds(1234));

            Assert.Equal(1000, started.EventId);
            Assert.Equal(LocalAuditLevel.Information, started.Level);
            Assert.Contains("task_id: 42", started.Message);
            Assert.Contains("command_sha256:", started.Message);
            Assert.Contains("command_length: " + command.Length, started.Message);
            Assert.Contains("requested_by: Pasha??forged", started.Message);
            Assert.DoesNotContain(command, started.Message);
            Assert.DoesNotContain("secret-password", started.Message);
            Assert.Equal(1001, finished.EventId);
            Assert.Contains("exit_code: 5", finished.Message);
            Assert.Contains("duration_ms: 1234", finished.Message);
        }

        [Fact]
        public void UpdateResult_IsAuditedOnceWhenFirstSeen()
        {
            // Updater sonucu ajan açıldıktan sonra yazar; kayıt sonuç ilk görüldüğünde bir kez üretilir
            var message = new Dictionary<string, object>
            {
                ["type"] = "update_result",
                ["status"] = "success",
                ["from_version"] = "0.1.11-alpha",
                ["to_version"] = "0.1.12-alpha",
                ["rollback"] = "none",
                ["detail"] = Guid.NewGuid().ToString("N"),
            };
            LocalAuditEvent first = AgentUpdate.PendingResultAudit(message);

            Assert.Equal(1030, first.EventId);
            Assert.Contains("from: 0.1.11-alpha", first.Message);
            Assert.Contains("to: 0.1.12-alpha", first.Message);
            Assert.Contains("outcome: success", first.Message);
            Assert.Null(AgentUpdate.PendingResultAudit(message));
            Assert.Null(AgentUpdate.PendingResultAudit(null));
        }

        [Fact]
        public void HighImpactEvents_HaveTheContractIdsAndFields()
        {
            LocalAuditEvent[] events =
            {
                LocalAudit.VisionStarted("S-1", "Pasha", true),
                LocalAudit.VisionFinished("S-1", "Pasha", true),
                LocalAudit.QuarantineStarted("panel"),
                LocalAudit.QuarantineFinished("bypass kodu"),
                LocalAudit.UpdateResult("0.1.11", "0.1.12", "rolled_back", "msi"),
                LocalAudit.CapabilityChanged("vision", true, false),
                LocalAudit.AuthenticationRejected("command"),
                LocalAudit.BypassSecretReceived("72cd6e8422c407fb"),
            };

            Assert.Equal(new[] { 1010, 1011, 1020, 1021, 1030, 1040, 1050, 1060 }, events.Select(e => e.EventId));
            Assert.Contains("session_id: S-1", events[0].Message);
            Assert.Contains("user_approved: True", events[0].Message);
            Assert.Contains("source: bypass kodu", events[3].Message);
            Assert.Contains("outcome: rolled_back", events[4].Message);
            Assert.Contains("old: True", events[5].Message);
            Assert.Equal(LocalAuditLevel.Warning, events[6].Level);
            Assert.Contains("fingerprint: 72cd6e8422c407fb", events[7].Message);
        }
    }
}
