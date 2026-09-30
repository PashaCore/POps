using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class VisionInputGateTests : TestBase
    {
        [Theory]
        [InlineData(false, true, true, true, "vision")]
        [InlineData(true, false, true, true, "consent")]
        [InlineData(true, true, false, true, "consent")]
        [InlineData(true, true, true, true, null)]
        [InlineData(true, false, false, false, null)]
        public void RemoteInput_RequiresCapabilityChannelAndConsent(bool enabled, bool approved, bool connected,
            bool inputEvent, string expectedDenial)
        {
            Assert.Equal(expectedDenial, VisionInputGate.DenialReason(enabled, approved, connected, inputEvent));
        }
    }
}
