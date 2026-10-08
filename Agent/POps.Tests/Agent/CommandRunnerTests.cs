using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Uzaktan komut çalıştırıcı: çıktı okunurken sınırlanır; iptal, servis durması ve süre sınırı işlemi sonlandırır;
    // çıkış kodu bildirilir. Testler yalnızca cmd.exe ile kısa, zararsız komutlar (echo, ping 127.0.0.1) çalıştırır.
    [Collection(MachineCollection.Name)]
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
            CommandExecutionResult result = await TestEnvironment.NewCommandRunner().RunAsync(1, "echo merhaba\r\nexit /b 3", CancellationToken.None);
            Assert.Equal(3, result.ExitCode);
            Assert.Contains("merhaba", result.Output);
        }

        [Fact]
        public async Task Cancel_KillsTheRunningProcess()
        {
            var runner = TestEnvironment.NewCommandRunner();
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
            Task<CommandExecutionResult> run = TestEnvironment.NewCommandRunner().RunAsync(7, "ping -n 30 127.0.0.1 > nul", stopping.Token);
            await Task.Delay(700);
            stopping.Cancel();

            CommandExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(CommandRunner.ExitServiceStopping, result.ExitCode);
        }

        [Fact]
        public async Task Timeout_KillsTheRunningProcess()
        {
            CommandExecutionResult result = await TestEnvironment.NewCommandRunner(TimeSpan.FromSeconds(1))
                .RunAsync(8, "ping -n 30 127.0.0.1 > nul", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(CommandRunner.ExitTimeout, result.ExitCode);
        }

        [Fact]
        public async Task FloodingOutput_IsBoundedWhileRunning()
        {
            // ~1 milyon karakter: sınır (512 bin) okunurken uygulanır, sonuç da sınırın altında kalır
            CommandExecutionResult result = await TestEnvironment.NewCommandRunner()
                .RunAsync(9, "for /L %%i in (1,1,20000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Output.Length <= CommandExecutionPolicy.MaxOutputChars);
            Assert.Contains("KISALTILDI", result.Output);
        }
    }

    // B1: çıktı satır sonu beklemeden sabit parçalarla okunur; B3: aynı görev kimliği iki kez çalışmaz
    [Collection(MachineCollection.Name)]
    public class CommandOutputChunkTests : TestBase
    {
        [Fact]
        public void Append_StopsAtTheLimit_AndCountsTheRest()
        {
            var output = new BoundedOutput(10);
            output.Append("12345678".ToCharArray(), 0, 8);
            output.Append("xxabcdefxx".ToCharArray(), 2, 6);
            Assert.Equal(4, output.DroppedChars);
            Assert.StartsWith("12345678ab", output.ToString());
            Assert.Contains("[ÇIKTI KISALTILDI: 4 karakter atıldı]", output.ToString());

            output.Append(null, 0, 5);
            output.Append("abc".ToCharArray(), 0, 0);
            Assert.Equal(4, output.DroppedChars);
        }

        [Fact]
        public void Append_And_AppendLine_ShareTheLimit()
        {
            var output = new BoundedOutput(6);
            output.AppendLine("ab");
            output.Append("cdefgh".ToCharArray(), 0, 6);
            Assert.StartsWith("ab\ncde", output.ToString());
            Assert.Equal(3, output.DroppedChars);
        }

        // 20 milyon karakter, hiç satır sonu yok: eskiden .NET'in satır okuyucusunda bütünüyle birikiyordu
        [Fact]
        public async Task HugeOutputWithoutNewlines_IsBoundedAndTheProcessFinishes()
        {
            CommandExecutionResult result = await TestEnvironment.NewCommandRunner()
                .RunAsync(11, "powershell -NoProfile -Command \"[Console]::Out.Write('x' * 20000000)\"", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Output.Length <= CommandExecutionPolicy.MaxOutputChars, result.Output.Length.ToString());
            Assert.Contains("KISALTILDI", result.Output);
            Assert.StartsWith("xxxx", result.Output);
        }

        [Fact]
        public async Task LineEndings_AreSentAsNewlines()
        {
            CommandExecutionResult result = await TestEnvironment.NewCommandRunner().RunAsync(12, "echo bir\r\necho iki", CancellationToken.None);
            Assert.Equal("bir\niki", result.Output);
        }

        [Fact]
        public async Task StandardError_IsReadInChunksToo()
        {
            CommandExecutionResult result = await TestEnvironment.NewCommandRunner().RunAsync(13, "echo hata-metni 1>&2\r\nexit /b 4", CancellationToken.None);
            Assert.Equal(4, result.ExitCode);
            Assert.Contains("[HATA]:\nhata-metni", result.Output);
        }

        [Fact]
        public async Task SameTaskId_DoesNotStartASecondProcess()
        {
            var runner = TestEnvironment.NewCommandRunner();
            Task<CommandExecutionResult> first = runner.RunAsync(21, "ping -n 30 127.0.0.1 > nul", CancellationToken.None);
            Assert.True(runner.IsRunning(21));

            CommandExecutionResult second = await runner.RunAsync(21, "echo ikinci", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CommandRunner.ExitDuplicate, second.ExitCode);
            Assert.Equal(1, runner.RunningCount);

            Assert.True(runner.Cancel(21));
            Assert.Equal(CommandRunner.ExitCancelled, (await first.WaitAsync(TimeSpan.FromSeconds(20))).ExitCode);
            Assert.False(runner.IsRunning(21));

            // Bittikten sonra aynı kimlik yeniden çalışabilir
            Assert.Equal(0, (await runner.RunAsync(21, "echo yeniden", CancellationToken.None)).ExitCode);
        }

        [Fact]
        public void ExitCodes_AreDistinct()
        {
            int[] codes = { CommandRunner.ExitTimeout, CommandRunner.ExitCancelled, CommandRunner.ExitAgentError, CommandRunner.ExitServiceStopping, CommandRunner.ExitDenied, CommandRunner.ExitDuplicate };
            Assert.Equal(codes.Length, codes.Distinct().Count());
            Assert.Equal((-5, -6), (CommandRunner.ExitDenied, CommandRunner.ExitDuplicate));
        }
    }

    // A4: açılışta yarım kalmış görev dosyaları silinir; başka dosyalara dokunulmaz
    [Collection(MachineCollection.Name)]
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

        // Görev dosyası çalıştırıcının klasörüne yazılır, görev bitince silinir; açılış temizliği (Worker) aynı klasöre
        // bakar. Testler gerçek %TEMP%'e yazmaz.
        [Fact]
        public async Task TaskFile_IsWrittenToTheRunnersFolder_AndCleanedUpThere()
        {
            CommandRunner runner = TestEnvironment.NewCommandRunner();
            string dir = runner.TaskDirectory;
            Assert.StartsWith(TestEnvironment.Root + Path.DirectorySeparatorChar, dir, StringComparison.OrdinalIgnoreCase);

            CommandExecutionResult result = await runner.RunAsync(1, "echo %~f0", CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(dir, Path.GetDirectoryName(result.Output), ignoreCase: true);
            Assert.True(CommandRunner.IsTaskFile(Path.GetFileName(result.Output)), result.Output);
            Assert.Empty(Directory.GetFiles(dir));

            // Çökmeden kalmış görev dosyası
            File.WriteAllText(Path.Combine(dir, "pops_task_" + Hex + ".bat"), "@echo off");
            Assert.Equal((1, 0), CommandRunner.CleanupStaleTaskFiles(runner.TaskDirectory));
            Assert.Empty(Directory.GetFiles(dir));
        }

        // Ajan değişmedi: klasör verilmezse görev dosyaları Path.GetTempPath() altına yazılır (burada yalnızca okunur)
        [Fact]
        public void TaskDirectory_DefaultsToTheTempFolder() =>
            Assert.Equal(Path.GetTempPath(), new CommandRunner().TaskDirectory);
    }
}
