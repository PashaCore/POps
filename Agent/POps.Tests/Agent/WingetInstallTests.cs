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
    // Machine: sahte winget gerçek bir süreç olarak çalışır
    [Collection(MachineCollection.Name)]
    public class WingetInstallTests : TestBase, IDisposable
    {
        // winget.exe'nin yeri (makine kapısı, her emirde sorulur; null: yok)
        private Func<string> _locator = () => null;
        // Testin Worker'larının bağlamı (sunucu modülleri dahil); winget kapısı _locator
        private readonly AgentHarness _agent;

        public WingetInstallTests()
        {
            _agent = AgentHarness.Create("winget", machine: new MachineSeams { Kiosk = new FakeKioskRegistry(), WingetLocator = () => _locator() });
            _agent.Capabilities.Load();
        }

        public void Dispose() => _agent.Dispose();

        // X-Agent-Features: bu bağlamın yetenek durumundan (bkz. CommandConnection)
        private AgentFeatures AgentFeatures => new AgentFeatures(_agent.Capabilities);

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

        // Yalnızca winget kaynağı (SYSTEM olarak msstore devreye girmez), önce --scope machine
        [Fact]
        public void Arguments_AreTheAgreedCommandLine()
        {
            Assert.Equal(new[] { "install", "--id", "Mozilla.Firefox", "-e", "--source", "winget", "--silent", "--scope", "machine",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" }, WingetInstall.Arguments("Mozilla.Firefox", null));
            Assert.Equal(new[] { "install", "--id", "7zip.7zip", "-e", "--source", "winget", "--silent", "--scope", "machine",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity", "--version", "24.08" }, WingetInstall.Arguments("7zip.7zip", "24.08"));
        }

        // Kapsamsız yeniden deneme: aynı komut satırı, yalnızca --scope machine yok
        [Fact]
        public void RetryArguments_DropOnlyTheScope()
        {
            Assert.Equal(new[] { "install", "--id", "Mozilla.Firefox", "-e", "--source", "winget", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" },
                WingetInstall.RetryArguments(WingetInstall.ExitNoApplicableInstaller, "Mozilla.Firefox", null));
            Assert.Equal(new[] { "install", "--id", "7zip.7zip", "-e", "--source", "winget", "--silent",
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity", "--version", "24.08" },
                WingetInstall.RetryArguments(WingetInstall.ExitNoApplicableInstaller, "7zip.7zip", "24.08"));
            Assert.Equal(WingetInstall.RetryArguments(WingetInstall.ExitNoApplicableInstaller, "7zip.7zip", "24.08"), WingetInstall.Arguments("7zip.7zip", "24.08", machineScope: false));
        }

        // winget belgesi (returnCodes.md): 0x8A150010 APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER, -1978335216
        [Fact]
        public void NoApplicableInstaller_IsWingetsCode() => Assert.Equal(-1978335216, WingetInstall.ExitNoApplicableInstaller);

        // Yalnızca "uygulanabilir yükleyici yok" yeniden denenir; başka hiçbir kod (başarı, zaten kurulu, paket yok, ajanın
        // kendi kodları) denenmez
        [Theory]
        [InlineData(0)]
        [InlineData(-1978335189)]   // 0x8A15002B zaten kurulu
        [InlineData(-1978335135)]   // 0x8A150061 zaten kurulu
        [InlineData(-1978335212)]   // 0x8A150014 paket yok
        [InlineData(-1978335215)]   // 0x8A150011 komşu kod
        [InlineData(-1978334967)]   // 0x8A150109 yeniden başlatma gerekli
        [InlineData(-1)]
        [InlineData(-2)]
        [InlineData(-3)]
        [InlineData(-4)]
        [InlineData(1)]
        public void OtherExitCodes_AreNotRetried(int exitCode) => Assert.Null(WingetInstall.RetryArguments(exitCode, "Mozilla.Firefox", null));

        [Fact]
        public void ResultOutput_StartsWithTheScope()
        {
            Assert.Equal("[kapsam: machine]\nFound Mozilla Firefox\nSuccessfully installed",
                WingetInstall.ResultOutput("Found Mozilla Firefox\r\n  ██████▒▒▒▒  1.0 MB / 2.0 MB\rSuccessfully installed\r\n"));
            Assert.Equal("[kapsam: user — machine yükleyici yok, kapsamsız yeniden denendi]\n[1. deneme, --scope machine: çıkış -1978335216]\n"
                + "No applicable installer found; see logs for more details.\n[2. deneme, kapsamsız]\nSuccessfully installed",
                WingetInstall.ResultOutput("   -\r   \\\r\nSuccessfully installed\r\n", "No applicable installer found; see logs for more details.\r\n"));
            // Yeniden denemede ilk denemenin çıktısı kısaltılır; sonuç sözleşmenin sınırını aşmaz
            string output = WingetInstall.ResultOutput(new string('b', CommandExecutionPolicy.MaxOutputChars), new string('a', 10000));
            Assert.StartsWith(WingetInstall.UserScopeNote + "\n[1. deneme, --scope machine: çıkış -1978335216]\n" + new string('a', WingetInstall.MachineAttemptMaxChars) + "\n[ÇIKTI KISALTILDI]\n[2. deneme, kapsamsız]\nbbb", output);
            Assert.True(output.Length <= CommandExecutionPolicy.MaxOutputChars);
            Assert.True(WingetInstall.ResultOutput(new string('b', CommandExecutionPolicy.MaxOutputChars)).Length <= CommandExecutionPolicy.MaxOutputChars);
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

        private Worker NewWorker() => new Worker(NullLogger<Worker>.Instance, _agent.Context)
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

        private void NothingMayRun() => _locator = () => throw new InvalidOperationException("çağrılmamalı");

        private void TerminalOff()
        {
            SecureStore.WriteProtected(_agent.Paths.SecureFile(AgentCapabilities.FileName), "{\"terminal_enabled\":false,\"vision_enabled\":true}");
            _agent.Capabilities.Load();
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
            _agent.Modules.Apply(JsonDocument.Parse("{\"modules\":{\"deploy\":false}}").RootElement);
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
            _locator = () => null;
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
            _locator = () => fake;
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(46, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            JsonElement result = await ResultFor(46);
            Assert.Equal(-1978335189, result.GetProperty("exit_code").GetInt32());
            Assert.Equal("[kapsam: machine]\nARGS:install --id Mozilla.Firefox -e --source winget --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity --version 131.0",
                result.GetProperty("output").GetString());
            Assert.Empty(Sent("capability_denied"));
        }

        // Sahte winget: her çağrının bağımsız değişkenlerini calls.txt'ye ve çıktıya yazar; --scope ile çağrılırsa scoped, yoksa
        // unscoped koduyla biter (unscopedPing: önce o kadar ping, yaklaşık saniye)
        private (string Fake, string Calls) FakeWinget(int scoped, int unscoped, int unscopedPing = 0)
        {
            string dir = TestEnvironment.NewDir("fake-winget");
            string fake = Path.Combine(dir, "winget.cmd");
            string calls = Path.Combine(dir, "calls.txt");
            File.WriteAllText(fake, "@echo off\r\n>>\"%~dp0calls.txt\" echo %*\r\necho ARGS:%*\r\nset scoped=0\r\n"
                + "for %%a in (%*) do if \"%%a\"==\"--scope\" set scoped=1\r\n"
                + $"if %scoped%==1 exit /b {scoped}\r\n"
                + (unscopedPing > 0 ? $"ping -n {unscopedPing} 127.0.0.1 > nul\r\n" : "")
                + $"exit /b {unscoped}\r\n");
            _locator = () => fake;
            return (fake, calls);
        }

        private static string[] Calls(string calls)
        {
            try { return File.Exists(calls) ? File.ReadAllLines(calls).Select(l => l.Trim()).ToArray() : Array.Empty<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
        }

        private const string MachineArgs = "install --id Mozilla.Firefox -e --source winget --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity";
        private const string UnscopedArgs = "install --id Mozilla.Firefox -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

        // Yalnızca kullanıcı kapsamında yükleyicisi olan paket: aynı görevde bir kez --scope olmadan; tek sonuç, ikinci denemenin
        // çıkış kodu, kapsam ilk satırda, iki denemenin çıktısı sırayla
        [Fact]
        public async Task NoMachineInstaller_IsRetriedOnceWithoutScope_AndReportsTheSecondAttempt()
        {
            (_, string calls) = FakeWinget(WingetInstall.ExitNoApplicableInstaller, -1978334967);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(48, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(48);
            Assert.Equal(-1978334967, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"{WingetInstall.UserScopeNote}\n[1. deneme, --scope machine: çıkış -1978335216]\nARGS:{MachineArgs}\n[2. deneme, kapsamsız]\nARGS:{UnscopedArgs}",
                result.GetProperty("output").GetString());
            Assert.Equal(new[] { MachineArgs, UnscopedArgs }, Calls(calls));

            // Sürümlü emirde iki deneme de sürümü taşır
            await worker.HandleServerMessageAsync(Install(49, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            await ResultFor(49);
            Assert.Equal(new[] { MachineArgs, UnscopedArgs, MachineArgs + " --version 131.0", UnscopedArgs + " --version 131.0" }, Calls(calls));
            await Task.Delay(200);
            Assert.Equal(1, Sent("result").Count(m => m.GetProperty("task_id").GetInt32() == 48));
            Assert.Equal(1, Sent("result").Count(m => m.GetProperty("task_id").GetInt32() == 49));
        }

        // Kapsamsız deneme de "uygulanabilir yükleyici yok" ile biterse üçüncü deneme yok: o kod bildirilir
        [Fact]
        public async Task NoInstallerAtAll_IsRetriedOnlyOnce()
        {
            (_, string calls) = FakeWinget(WingetInstall.ExitNoApplicableInstaller, WingetInstall.ExitNoApplicableInstaller);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(50, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(50);
            Assert.Equal(-1978335216, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith(WingetInstall.UserScopeNote + "\n", result.GetProperty("output").GetString());
            Assert.Equal(new[] { MachineArgs, UnscopedArgs }, Calls(calls));
        }

        // Başka bir hata kodu yeniden denenmez: tek çağrı, kapsam machine
        [Theory]
        [InlineData(-1978335212)]
        [InlineData(0)]
        public async Task OtherExitCodes_RunOnce_WithMachineScope(int exitCode)
        {
            (_, string calls) = FakeWinget(exitCode, 0);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(51, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(51);
            Assert.Equal(exitCode, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"[kapsam: machine]\nARGS:{MachineArgs}", result.GetProperty("output").GetString());
            Assert.Equal(new[] { MachineArgs }, Calls(calls));
        }

        // Kapsamsız deneme aynı görevdir: sürerken gelen aynı emir yeni bir kurulum başlatmaz, cancel_task onu sonlandırır, tek sonuç
        [Fact]
        public async Task Retry_IsTheSameTask_ForDuplicatesAndCancel()
        {
            (_, string calls) = FakeWinget(WingetInstall.ExitNoApplicableInstaller, 0, unscopedPing: 30);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(52, "Mozilla.Firefox"), null, CancellationToken.None);
            for (int i = 0; i < 200 && Calls(calls).Length < 2; i++) await Task.Delay(50);
            Assert.Equal(new[] { MachineArgs, UnscopedArgs }, Calls(calls));

            await worker.HandleServerMessageAsync(Install(52, "Mozilla.Firefox"), null, CancellationToken.None);
            await Task.Delay(300);
            Assert.Equal(2, Calls(calls).Length);

            await worker.HandleServerMessageAsync("{\"action\":\"cancel_task\",\"task_id\":52}", null, CancellationToken.None);
            JsonElement result = await ResultFor(52);
            Assert.Equal(CommandRunner.ExitCancelled, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith(WingetInstall.UserScopeNote + "\n", result.GetProperty("output").GetString());
            Assert.Contains("[2. deneme, kapsamsız]\n[İPTAL EDİLDİ]", result.GetProperty("output").GetString());
            await Task.Delay(300);
            Assert.Equal(1, Sent("result").Count(m => m.GetProperty("task_id").GetInt32() == 52));
            Assert.Equal(2, Calls(calls).Length);
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
