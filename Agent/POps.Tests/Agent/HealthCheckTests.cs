using System;
using System.Collections.Generic;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class HealthCheckTests : TestBase
    {
        private static readonly DateTime NotBefore = DateTimeOffset.FromUnixTimeSeconds(1000).UtcDateTime;

        [Theory]
        [InlineData("{\"version\":\"0.1.12-alpha\",\"ts\":1000,\"phase\":\"operational\"}", true)]
        [InlineData("{\"version\":\"0.1.12-alpha\",\"ts\":1000}", true)] // eski ajan / rollback
        [InlineData("{\"version\":\"0.1.12-alpha\",\"ts\":999,\"phase\":\"operational\"}", false)]
        [InlineData("{\"version\":\"0.1.11-alpha\",\"ts\":1000,\"phase\":\"operational\"}", false)]
        [InlineData("{\"version\":\"0.1.12-alpha\",\"ts\":1000,\"phase\":\"process_started\"}", false)]
        [InlineData("{broken", false)]
        public void HealthDecision_RequiresVersionTimestampAndOperationalWhenPhaseExists(string json, bool expected) =>
            Assert.Equal(expected, HealthCheck.IsHealthy(json, "0.1.12-alpha", NotBefore));

        [Fact]
        public void OperationalHealth_IsWrittenOnlyAfterEveryCheck()
        {
            var writes = new List<OperationalChecks>();
            var health = new AgentStartupHealth(false, writes.Add);
            health.Mark(StartupCheck.Identity);
            health.Mark(StartupCheck.Credentials);
            health.Mark(StartupCheck.Capabilities);
            health.Mark(StartupCheck.Pipe);
            Assert.Empty(writes);
            health.Mark(StartupCheck.Loop);
            Assert.Single(writes);
            Assert.True(writes[0].Complete);
            health.Mark(StartupCheck.Loop);
            Assert.Single(writes);
        }

        [Fact]
        public void StartupException_DoesNotCompleteOrWriteHealth()
        {
            var writes = new List<OperationalChecks>();
            var health = new AgentStartupHealth(false, writes.Add);
            Assert.Throws<InvalidOperationException>(() => health.Run(StartupCheck.Identity, () => throw new InvalidOperationException("açılış hatası")));
            health.Mark(StartupCheck.Credentials);
            health.Mark(StartupCheck.Capabilities);
            health.Mark(StartupCheck.Pipe);
            health.Mark(StartupCheck.Loop);
            Assert.Empty(writes);
            Assert.False(health.Snapshot().Identity);
        }

        [Fact]
        public void RollbackDrill_SuppressesOperationalHealth()
        {
            var writes = new List<OperationalChecks>();
            var health = new AgentStartupHealth(true, writes.Add);
            foreach (StartupCheck check in Enum.GetValues<StartupCheck>()) health.Mark(check);
            Assert.Empty(writes);
            Assert.True(health.Snapshot().Complete);
        }
    }
}
