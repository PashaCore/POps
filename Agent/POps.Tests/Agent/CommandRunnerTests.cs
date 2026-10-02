using System;
using System.Threading;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Uzaktan komut çalıştırıcı: çıktı okunurken sınırlanır; iptal, servis durması ve süre sınırı işlemi sonlandırır;
    // çıkış kodu bildirilir. Testler yalnızca cmd.exe ile kısa, zararsız komutlar (echo, ping 127.0.0.1) çalıştırır.
    public class CommandRunnerTests : TestBase
    {
        [Fact]
        public void BoundedOutput_StopsGrowingAtTheLimit()
        {
            var output = new BoundedOutput(10);
            output.AppendLine("12345");
            output.AppendLine("67890");
            output.AppendLine("abc");

            string text = output.ToString();
            Assert.StartsWith("12345\n6789", text);
            Assert.Equal(6, output.DroppedChars);
            Assert.Contains("[ÇIKTI KISALTILDI: 6 karakter atıldı]", text);
        }

        [Fact]
        public void BoundedOutput_UnderTheLimit_IsUnchanged()
        {
            var output = new BoundedOutput(100);
            output.AppendLine("a");
            output.AppendLine(null);
            output.AppendLine("b");
            Assert.Equal("a\nb\n", output.ToString());
            Assert.Equal(0, output.DroppedChars);
        }

        [Fact]
        public async Task ExitCode_IsReported()
        {
            CommandExecutionResult result = await new CommandRunner().RunAsync(1, "echo merhaba\r\nexit /b 3", CancellationToken.None);
            Assert.Equal(3, result.ExitCode);
            Assert.Contains("merhaba", result.Output);
        }

        [Fact]
        public async Task Cancel_KillsTheRunningProcess()
        {
            var runner = new CommandRunner();
            Task<CommandExecutionResult> run = runner.RunAsync(42, "ping -n 30 127.0.0.1 > nul", CancellationToken.None);
            for (int i = 0; i < 100 && runner.RunningCount == 0; i++) await Task.Delay(20);
            await Task.Delay(500);

            Assert.True(runner.Cancel(42));
            CommandExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(CommandRunner.ExitCancelled, result.ExitCode);
            Assert.Contains("İPTAL EDİLDİ", result.Output);
            Assert.True(result.Duration < TimeSpan.FromSeconds(20));
            Assert.Equal(0, runner.RunningCount);
            Assert.False(runner.Cancel(42));
        }

        [Fact]
        public async Task ServiceStop_KillsTheRunningProcess()
        {
            using var stopping = new CancellationTokenSource();
            Task<CommandExecutionResult> run = new CommandRunner().RunAsync(7, "ping -n 30 127.0.0.1 > nul", stopping.Token);
            await Task.Delay(700);
            stopping.Cancel();

            CommandExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(CommandRunner.ExitServiceStopping, result.ExitCode);
        }

        [Fact]
        public async Task Timeout_KillsTheRunningProcess()
        {
            CommandExecutionResult result = await new CommandRunner(maxDuration: TimeSpan.FromSeconds(1))
                .RunAsync(8, "ping -n 30 127.0.0.1 > nul", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(CommandRunner.ExitTimeout, result.ExitCode);
        }

        [Fact]
        public async Task FloodingOutput_IsBoundedWhileRunning()
        {
            // ~1 milyon karakter: sınır (512 bin) okunurken uygulanır, sonuç da sınırın altında kalır
            CommandExecutionResult result = await new CommandRunner()
                .RunAsync(9, "for /L %%i in (1,1,20000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Output.Length <= CommandExecutionPolicy.MaxOutputChars);
            Assert.Contains("KISALTILDI", result.Output);
        }
    }
}
