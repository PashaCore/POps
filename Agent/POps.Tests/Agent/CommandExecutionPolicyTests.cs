using System;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class CommandExecutionPolicyTests : TestBase
    {
        [Fact]
        public void DisabledTerminal_IsRejectedWithTheResultText()
        {
            CommandPermission denied = CommandExecutionPolicy.Permission(false);
            Assert.False(denied.Allowed);
            Assert.Equal(CommandExecutionPolicy.DisabledMessage, denied.Rejection);
            Assert.True(CommandExecutionPolicy.Permission(true).Allowed);
        }

        [Theory]
        [InlineData(0, 1800)]
        [InlineData(60, 1740)]
        [InlineData(1799, 1)]
        [InlineData(1800, 0)]
        [InlineData(2000, 0)]
        public void Timeout_IsThirtyMinutesMinusElapsedSeconds(int elapsedSeconds, int expectedSeconds)
        {
            DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(1000);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
                CommandExecutionPolicy.RemainingTimeout(start, start.AddSeconds(elapsedSeconds)));
        }

        [Fact]
        public void Output_IsBoundedAndStatesTheOriginalLength()
        {
            string shortOutput = "ok";
            Assert.Same(shortOutput, CommandExecutionPolicy.TruncateOutput(shortOutput));

            string longOutput = new string('x', CommandExecutionPolicy.MaxOutputChars + 123);
            string truncated = CommandExecutionPolicy.TruncateOutput(longOutput);
            Assert.Equal(CommandExecutionPolicy.MaxOutputChars, truncated.Length);
            Assert.StartsWith(new string('x', 100), truncated);
            Assert.EndsWith($"[ÇIKTI KISALTILDI: toplam {longOutput.Length} karakter]", truncated);
        }
    }
}
