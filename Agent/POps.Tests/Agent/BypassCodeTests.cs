using POps.Shared;
using Xunit;

namespace POps.Tests.Agent
{
    // Tepsi ve servis aynı bypass kodu biçimini kullanır; biçim hatası servise gitmez (L3)
    public class BypassCodeTests : TestBase
    {
        [Theory]
        [InlineData("372cc1", "372CC1")]
        [InlineData(" 372CC1 ", "372CC1")]
        [InlineData("0123456789abcdef", "0123456789ABCDEF")]
        public void WellFormedCodes_AreNormalized(string code, string expected) => Assert.Equal(expected, BypassCode.Normalize(code));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("O72CC1")]   // O harfi, 0 değil
        [InlineData("12345")]
        [InlineData("372 CC1")]
        [InlineData("GHIJKL")]
        public void MalformedCodes_AreRejected(string code) => Assert.False(BypassCode.IsWellFormed(code));

        [Fact]
        public void LengthLimits()
        {
            Assert.True(BypassCode.IsWellFormed(new string('A', 64)));
            Assert.False(BypassCode.IsWellFormed(new string('A', 65)));
        }
    }
}
