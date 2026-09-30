using System.Collections.Generic;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class BypassSecretCommandTests : TestBase
    {
        private const string Secret = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE";

        [Fact]
        public void AuthenticatedCommand_SavesAndReturnsFingerprint_WithoutLoggingSecret()
        {
            var logs = new List<string>();
            string saved = null;

            bool accepted = BypassSecretCommand.Process(Secret, true, value => { saved = value; return true; }, logs.Add, out string fingerprint);

            Assert.True(accepted);
            Assert.Equal(Secret, saved);
            Assert.Equal("72cd6e8422c407fb", fingerprint);
            Assert.DoesNotContain(logs, line => line.Contains(Secret));
        }

        [Theory]
        [InlineData(false, Secret)]
        [InlineData(true, "not-base64url")]
        public void UnauthenticatedOrMalformedCommand_IsRejected(bool authenticated, string secret)
        {
            bool wrote = false;
            Assert.False(BypassSecretCommand.Process(secret, authenticated, _ => { wrote = true; return true; }, null, out string fingerprint));
            Assert.False(wrote);
            Assert.Null(fingerprint);
        }
    }
}
