using System;
using System.Text.Json;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    [Collection(SharedStateCollection.Name)]
    public class AgentHealthTelemetryTests : SharedStateTestBase
    {
        [Fact]
        public void ErrorCounter_UsesASlidingOneHourWindow()
        {
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1000);
            var health = new AgentHealthTelemetry(() => now);
            health.RecordError("policy", "first");
            now = now.AddMinutes(59);
            health.RecordError("dns", "second");

            Assert.Equal(2, health.Snapshot(false, true, false).LoopErrors1h);
            now = now.AddMinutes(2);
            AgentHealthSnapshot snapshot = health.Snapshot(false, true, false);
            Assert.Equal(1, snapshot.LoopErrors1h);
            Assert.Equal("dns: second", snapshot.LastError);
        }

        [Fact]
        public void LastError_IsSanitizedAndAtMostTwoHundredCharacters()
        {
            var health = new AgentHealthTelemetry();
            health.RecordError("inventory", "bad\r\n" + new string('x', 300));
            string error = health.Snapshot(false, true, false).LastError;

            Assert.DoesNotContain("\r", error);
            Assert.DoesNotContain("\n", error);
            Assert.True(error.Length <= 200);
            Assert.EndsWith("…", error);
        }

        [Fact]
        public void Snapshot_HasTimestampsAndVisionStates()
        {
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1234);
            var health = new AgentHealthTelemetry(() => now);
            now = DateTimeOffset.FromUnixTimeSeconds(1300);
            health.PolicySynced();
            now = DateTimeOffset.FromUnixTimeSeconds(1400);
            health.InventoryUploaded();

            AgentHealthSnapshot snapshot = health.Snapshot(true, true, true);
            Assert.Equal(1234, snapshot.StartedAt);
            Assert.Equal(1300, snapshot.LastPolicySync);
            Assert.Equal(1400, snapshot.LastInventoryUpload);
            Assert.True(snapshot.TrayConnected);
            Assert.Equal("connected", snapshot.VisionChannel);
            Assert.Equal("idle", AgentHealthTelemetry.VisionState(true, false));
            Assert.Equal("off", AgentHealthTelemetry.VisionState(false, true));
        }

        [Fact]
        public void Heartbeat_ContainsTheHealthContract()
        {
            using var worker = new Worker(Microsoft.Extensions.Logging.Abstractions.NullLogger<Worker>.Instance, AgentHarness.FromStatics().Context);
            JsonElement health = JsonSerializer.SerializeToElement(worker.HeartbeatPayload()).GetProperty("agent_health");

            Assert.Equal(JsonValueKind.Number, health.GetProperty("started_at").ValueKind);
            Assert.Equal(JsonValueKind.Null, health.GetProperty("last_policy_sync").ValueKind);
            Assert.Equal(JsonValueKind.Null, health.GetProperty("last_inventory_upload").ValueKind);
            Assert.Equal(JsonValueKind.False, health.GetProperty("tray_connected").ValueKind);
            Assert.Contains(health.GetProperty("vision_channel").GetString(), new[] { "off", "idle" });
            Assert.Equal(0, health.GetProperty("loop_errors_1h").GetInt32());
            Assert.Equal("", health.GetProperty("last_error").GetString());
        }

        [Fact]
        public void QuarantineState_IsReportedSeparately()
        {
            var health = new AgentHealthTelemetry(() => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
            AgentHealthSnapshot snapshot = health.Snapshot(true, false, false, true, false, new string('x', 300));
            Assert.True(snapshot.ScreenLocked);
            Assert.False(snapshot.NetworkIsolated);
            Assert.True(snapshot.IsolationError.Length <= 200);
            string json = System.Text.Json.JsonSerializer.Serialize(snapshot);
            Assert.Contains("\"screen_locked\":true", json);
            Assert.Contains("\"network_isolated\":false", json);
        }
    }
}
