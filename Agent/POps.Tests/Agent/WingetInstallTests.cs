using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // winget_install (docs/agent.md "winget_install contract"): doğrulama, bağımsız değişkenler, winget'in bulunması,
    // kabuksuz çalıştırma, yetenek/modül retleri, -7, çıktının temizlenmesi, X-Agent-Features
    public class WingetInstallTests : TestBase, IDisposable
    {
        private readonly Func<string> _locator = WingetInstall.Locator;

        public WingetInstallTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("winget-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("winget-data");
            AgentCapabilities.Load();
        }

        public void Dispose()
        {
            WingetInstall.Locator = _locator;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentCapabilities.Load();
        }

        private static JsonElement Command(string json) => JsonDocument.Parse(json).RootElement.Clone();

        // Sözleşmedeki örnek (docs/agent.md, examples/server-to-agent/winget_install.json)
        private const string Latest = "{\"action\":\"winget_install\",\"task_id\":42,\"id\":\"Mozilla.Firefox\",\"version\":null,\"requested_by\":\"admin\"}";

        [Theory]
        [InlineData("{\"id\":\"Mozilla.Firefox\",\"version\":null}", "Mozilla.Firefox", null)]
        [InlineData("{\"id\":\"7zip.7zip\",\"version\":\"24.08\"}", "7zip.7zip", "24.08")]
        [InlineData("{\"id\":\"Notepad++.Notepad++\"}", "Notepad++.Notepad++", null)]
        [InlineData("{\"id\":\"Microsoft.VCRedist.2015+.x64\",\"version\":\"14.40.33810.0\"}", "Microsoft.VCRedist.2015+.x64", "14.40.33810.0")]
        public void ValidCommands(string json, string id, string version)
        {
            Assert.True(WingetInstall.TryParse(Command(json), out string parsedId, out string parsedVersion));
            Assert.Equal((id, version), (parsedId, parsedVersion));
        }

        [Theory]
        [InlineData("{\"id\":\"a b\"}")]
        [InlineData("{\"id\":\"x&del\"}")]
        [InlineData("{\"id\":\"-h\"}")]
        [InlineData("{\"id\":\"A\"}")]
        [InlineData("{\"id\":\"Mozilla.Firefox\\n\"}")]
        [InlineData("{\"id\":\"Mozilla.Firefox\",\"version\":\"1 | calc\"}")]
        [InlineData("{\"id\":\"Mozilla.Firefox\",\"version\":\"1.0\\n\"}")]
        [InlineData("{\"id\":\"Mozilla.Firefox\",\"version\":5}")]
        [InlineData("{\"id\":\"Mozilla.Firefox\",\"version\":\"\"}")]
        [InlineData("{\"id\":5}")]
        [InlineData("{}")]
        public void InvalidCommands_AreRefused(string json) => Assert.False(WingetInstall.TryParse(Command(json), out _, out _));

        [Fact]
        public void LengthLimits()
        {
            Assert.True(WingetInstall.TryParse(Command($"{{\"id\":\"A{new string('b', 127)}\"}}"), out _, out _));
            Assert.False(WingetInstall.TryParse(Command($"{{\"id\":\"A{new string('b', 128)}\"}}"), out _, out _));
            Assert.True(WingetInstall.TryParse(Command($"{{\"id\":\"Ab\",\"version\":\"{new string('1', 40)}\"}}"), out _, out _));
            Assert.False(WingetInstall.TryParse(Command($"{{\"id\":\"Ab\",\"version\":\"{new string('1', 41)}\"}}"), out _, out _));
        }

        [Fact]
        public void Arguments_AreTheAgreedCommandLine()
        {
            Assert.Equal(new[] { "install", "--id", "Mozilla.Firefox", "-e", "--silent", "--scope", "machine", "--accept-package-agreements",
                "--accept-source-agreements", "--disable-interactivity" }, WingetInstall.Arguments("Mozilla.Firefox", null));
            Assert.Equal(new[] { "--version", "24.08" }, WingetInstall.Arguments("7zip.7zip", "24.08").TakeLast(2));
        }

        [Fact]
        public void Output_DropsProgressBarsAndSpinners()
        {
            string raw = "Found Mozilla Firefox [Mozilla.Firefox] Version 131.0\r\n   -\r   \\\r   |\r   /\r\n"
                + "  ██████████▒▒▒▒▒▒▒▒  40.0 MB / 70.0 MB\r  ████████████████████  70.0 MB / 70.0 MB\r\n"
                + "Successfully verified installer hash\r\nStarting package install...\r\nSuccessfully installed\r\n";
            Assert.Equal("Found Mozilla Firefox [Mozilla.Firefox] Version 131.0\nSuccessfully verified installer hash\nStarting package install...\nSuccessfully installed",
                WingetInstall.CleanOutput(raw));
            Assert.Equal("", WingetInstall.CleanOutput(""));
        }

        [Fact]
        public void FeatureHeader_AnnouncesWinget()
        {
            Assert.Equal("X-Agent-Features", AgentFeatures.HeaderName);
            Assert.Contains("winget", AgentFeatures.Header.Split(','));
            Assert.Matches("^[a-z0-9_]+(,[a-z0-9_]+)*$", AgentFeatures.Header);
        }

        [Fact]
        public void Find_PicksTheNewestForThisArchitecture()
        {
            string apps = TestEnvironment.NewDir("WindowsApps");
            string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            string other = arch == "x64" ? "arm64" : "x64";
            void Package(string version, string a, bool exe = true)
            {
                string dir = Path.Combine(apps, $"Microsoft.DesktopAppInstaller_{version}_{a}__8wekyb3d8bbwe");
                Directory.CreateDirectory(dir);
                if (exe) File.WriteAllText(Path.Combine(dir, "winget.exe"), "");
            }
            Assert.Null(WingetInstall.Find(apps));
            Package("1.21.3482.0", arch);
            Package("1.23.1911.0", arch);
            Package("1.24.0.0", arch, exe: false);
            Package("9.0.0.0", other);
            Directory.CreateDirectory(Path.Combine(apps, "Microsoft.DesktopAppInstaller_1.0.0.0_neutral_~_8wekyb3d8bbwe"));
            Assert.Equal(Path.Combine(apps, $"Microsoft.DesktopAppInstaller_1.23.1911.0_{arch}__8wekyb3d8bbwe", "winget.exe"), WingetInstall.Find(apps));
            Assert.Null(WingetInstall.Find(Path.Combine(apps, "yok")));
        }

        [Fact]
        public async Task RunProgram_PassesArgumentsWithoutAShell()
        {
            var runner = TestEnvironment.NewCommandRunner();
            // Bağımsız değişkenler ArgumentList ile tek tek gider (kabuk yorumlamaz)
            CommandExecutionResult result = await runner.RunProgramAsync(1, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new[] { "/d", "/c", "echo", "winget-ok" }, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("winget-ok", result.Output);

            Task<CommandExecutionResult> first = runner.RunProgramAsync(2, Path.Combine(Environment.SystemDirectory, "ping.exe"), new[] { "-n", "3", "127.0.0.1" }, CancellationToken.None);
            CommandExecutionResult duplicate = await runner.RunProgramAsync(2, "ping.exe", Array.Empty<string>(), CancellationToken.None);
            Assert.Equal(CommandRunner.ExitDuplicate, duplicate.ExitCode);
            Assert.True(runner.Cancel(2));
            Assert.Equal(CommandRunner.ExitCancelled, (await first).ExitCode);
        }

        private readonly List<JsonElement> _sent = new List<JsonElement>();

        private Worker NewWorker() => new Worker(NullLogger<Worker>.Instance)
        {
            HwId = "HW-WINGET",
            SendOverride = p => { lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(p)); return Task.FromResult(true); },
        };

        private List<JsonElement> Sent(string type)
        {
            lock (_sent) return _sent.Where(m => m.GetProperty("type").GetString() == type).ToList();
        }

        private async Task<JsonElement> ResultFor(int taskId)
        {
            for (int i = 0; i < 200; i++)
            {
                JsonElement found = Sent("result").FirstOrDefault(m => m.GetProperty("task_id").GetInt32() == taskId);
                if (found.ValueKind != JsonValueKind.Undefined) return found;
                await Task.Delay(50);
            }
            throw new TimeoutException();
        }

        private static string Install(int taskId, string id, string version = null) =>
            JsonSerializer.Serialize(new { action = "winget_install", task_id = taskId, id, version, requested_by = "admin" });

        private static void NothingMayRun() => WingetInstall.Locator = () => throw new InvalidOperationException("çağrılmamalı");

        private static void TerminalOff()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":false,\"vision_enabled\":true}");
            AgentCapabilities.Load();
        }

        [Fact]
        public async Task TerminalCapabilityOff_IsRefused_WithCapabilityDenied()
        {
            NothingMayRun();
            TerminalOff();
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Latest, null, CancellationToken.None);
            JsonElement result = await ResultFor(42);
            Assert.Equal((WingetInstall.TerminalOffMessage, -5), (result.GetProperty("output").GetString(), result.GetProperty("exit_code").GetInt32()));
            JsonElement denied = Assert.Single(Sent("capability_denied"));
            Assert.Equal(("terminal", "winget_install", 42), (denied.GetProperty("capability").GetString(), denied.GetProperty("action").GetString(), denied.GetProperty("task_id").GetInt32()));
            Assert.False(denied.TryGetProperty("reason", out _));
        }

        [Fact]
        public async Task DeployModuleOff_IsRefused_WithModuleDisabled()
        {
            NothingMayRun();
            AgentModules.Apply(JsonDocument.Parse("{\"modules\":{\"deploy\":false}}").RootElement);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(43, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(43);
            Assert.Equal(-5, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith("[REDDEDİLDİ]", result.GetProperty("output").GetString());
            JsonElement denied = Assert.Single(Sent("capability_denied"));
            Assert.Equal(("deploy", "winget_install", 43, "module_disabled"), (denied.GetProperty("capability").GetString(), denied.GetProperty("action").GetString(),
                denied.GetProperty("task_id").GetInt32(), denied.GetProperty("reason").GetString()));
        }

        [Fact]
        public async Task InvalidPackage_IsRefused_WithoutCapabilityDenied()
        {
            NothingMayRun();
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(44, "x & y"), null, CancellationToken.None);
            JsonElement result = await ResultFor(44);
            Assert.Equal((WingetInstall.InvalidMessage, -5), (result.GetProperty("output").GetString(), result.GetProperty("exit_code").GetInt32()));
            Assert.Empty(Sent("capability_denied"));
        }

        [Fact]
        public async Task WingetMissing_IsMinusSeven()
        {
            WingetInstall.Locator = () => null;
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(45, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(45);
            Assert.Equal(("[REDDEDİLDİ] winget bu bilgisayarda yok", -7), (result.GetProperty("output").GetString(), result.GetProperty("exit_code").GetInt32()));
            Assert.Empty(Sent("capability_denied"));
        }

        [Fact]
        public async Task CommandWithoutTaskId_IsIgnored()
        {
            NothingMayRun();
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync("{\"action\":\"winget_install\",\"id\":\"Mozilla.Firefox\"}", null, CancellationToken.None);
            await Task.Delay(100);
            lock (_sent) Assert.Empty(_sent);
        }

        // winget yerine bağımsız değişkenleri yankılayan bir program: komut satırı kabuksuz ve beklenen biçimde; çıkış kodu
        // winget'in işaretli 32 bit kodu olarak aynen gider
        [Fact]
        public async Task Install_RunsTheProgramWithTheAgreedArguments_AndReportsItsExitCode()
        {
            string fake = Path.Combine(TestEnvironment.NewDir("fake-winget"), "winget.cmd");
            File.WriteAllText(fake, "@echo off\r\necho ARGS:%*\r\nexit /b -1978335189\r\n");
            WingetInstall.Locator = () => fake;
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(46, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            JsonElement result = await ResultFor(46);
            Assert.Equal(-1978335189, result.GetProperty("exit_code").GetInt32());
            Assert.Contains("ARGS:install --id Mozilla.Firefox -e --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity --version 131.0",
                result.GetProperty("output").GetString());
            Assert.Empty(Sent("capability_denied"));
        }

        // Eski "POPS-WINGET" yolu yok: execute yalnızca komuttur (terminal kapalıyken her zamanki ret)
        [Fact]
        public async Task Execute_HasNoWingetMarker()
        {
            NothingMayRun();
            TerminalOff();
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(JsonSerializer.Serialize(new { action = "execute", task_id = 47, script_path = "POPS-WINGET {\"id\":\"Mozilla.Firefox\"}" }), null, CancellationToken.None);
            JsonElement result = await ResultFor(47);
            Assert.Equal(-5, result.GetProperty("exit_code").GetInt32());
            Assert.Equal("execute", Assert.Single(Sent("capability_denied")).GetProperty("action").GetString());
        }
    }
}
