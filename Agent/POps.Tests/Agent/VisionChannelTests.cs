using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    public class VisionChannelTests : TestBase
    {
        [Theory]
        [InlineData("device-secret", "enroll-token", true)]
        [InlineData("device-secret", null, true)]
        [InlineData(null, "enroll-token", false)]
        [InlineData(null, null, false)]
        public void VisionHeaders_UseOnlyDeviceSecret(string secret, string token, bool canConnect)
        {
            VisionAuthSelection selected = VisionChannel.SelectHeaders(secret, token, "0.1.12-alpha");
            Assert.Equal(canConnect, selected.CanConnect);
            Assert.Equal("0.1.12-alpha", selected.Headers["X-Agent-Version"]);
            Assert.Equal(secret != null, selected.Headers.ContainsKey("X-Agent-Secret"));
            Assert.False(selected.Headers.ContainsKey("X-Enroll-Token"));
        }

        [Theory]
        [InlineData(4401, true)]
        [InlineData(1000, false)]
        [InlineData(null, false)]
        public void ClosingVision_ClearsStateAndNeverRetriesAutomatically(int? status, bool rejected)
        {
            VisionCloseDecision decision = VisionChannel.OnClosed(status);
            Assert.Equal(rejected, decision.AuthenticationRejected);
            Assert.True(decision.ClearStream);
            Assert.True(decision.ClearApproval);
            Assert.False(decision.RetryAutomatically);
        }
    }
}
