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
    // kabuksuz çalıştırma, yetenek/modül retleri, -7, çıktının temizlenmesi, X-Agent-Features, sürümsüz kurulumun en yeni
    // sürüme sabitlenmesi, paketin kurulduğu yer. Gerçek winget hiçbir testte çalışmaz: winget yerine testin yazdığı sahte bir
    // .cmd çalışır. Bu bilgisayarın kayıt defteri okunmaz: kurulu yazılım kayıtları testin sahte görüntüleridir.
    // Machine: sahte winget gerçek bir süreç olarak çalışır
    [Collection(MachineCollection.Name)]
    public class WingetInstallTests : TestBase, IDisposable
    {
        // winget.exe'nin yeri (makine kapısı, her emirde sorulur; null: yok)
        private Func<string> _locator = () => null;
        // Testin Worker'larının bağlamı (sunucu modülleri dahil); winget kapısı _locator, kayıt defteri kapısı ReadRegistry
        private readonly AgentHarness _agent;
        // Kurulu yazılım kayıtlarının sahte görüntüleri (makine kapısı MachineSeams.InstallRegistry): her okuma sıradakini verir,
        // sıra bitince sonuncusu tekrarlanır (hiç verilmediyse kayıt yok). Her okumada sahte winget'in o ana kadarki çağrı sayısı
        // yazılır: okumanın kurulumdan önce mi sonra mı olduğu.
        private readonly Queue<Func<InstallRegistrySnapshot>> _snapshots = new Queue<Func<InstallRegistrySnapshot>>();
        private Func<InstallRegistrySnapshot> _lastSnapshot = InstallRegistrySnapshot.Nothing;
        private readonly List<int> _registryReads = new List<int>();
        private string _calls;

        public WingetInstallTests()
        {
            _agent = AgentHarness.Create("winget", machine: new MachineSeams
            {
                Kiosk = new FakeKioskRegistry(),
                WingetLocator = () => _locator(),
                InstallRegistry = ReadRegistry,
            });
            _agent.Capabilities.Load();
        }

        private InstallRegistrySnapshot ReadRegistry()
        {
            Func<InstallRegistrySnapshot> next;
            lock (_snapshots)
            {
                _registryReads.Add(_calls == null ? 0 : Calls(_calls).Length);
                if (_snapshots.Count > 0) _lastSnapshot = _snapshots.Dequeue();
                next = _lastSnapshot;
            }
            return next();
        }

        private void Registry(params Func<InstallRegistrySnapshot>[] snapshots)
        {
            lock (_snapshots) foreach (Func<InstallRegistrySnapshot> snapshot in snapshots) _snapshots.Enqueue(snapshot);
        }

        private int[] RegistryReads()
        {
            lock (_snapshots) return _registryReads.ToArray();
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

        // Sürümü ajan seçtiyse ikinci satır o sürümdür (ya da neden seçemediği); yeniden denemede de kapsam satırından hemen sonra
        [Fact]
        public void ResultOutput_PutsTheVersionNoteAfterTheScope()
        {
            Assert.Equal("[kapsam: machine]\n[sürüm: kaynaktaki en yeni sürüm 26.09.3 seçildi]\nSuccessfully installed",
                WingetInstall.ResultOutput("Successfully installed\r\n", null, WingetInstall.PinnedNote("26.09.3")));
            Assert.Equal("[kapsam: user — machine yükleyici yok, kapsamsız yeniden denendi]\n[sürüm: kaynaktaki en yeni sürüm 26.09.3 seçildi]\n"
                + "[1. deneme, --scope machine: çıkış -1978335216]\nNo applicable installer found\n[2. deneme, kapsamsız]\nSuccessfully installed",
                WingetInstall.ResultOutput("Successfully installed", "No applicable installer found", WingetInstall.PinnedNote("26.09.3")));
            Assert.Equal("[kapsam: machine]\n[sürüm: en yeni sürüm bulunamadı (winget show çıkış kodu -1978335212); winget kendisi seçti]\nx",
                WingetInstall.ResultOutput("x", null, WingetInstall.UnpinnedNote("winget show çıkış kodu -1978335212")));
            Assert.Equal("[sürüm: en yeni sürüm sorgulanırken görev durdu; kurulum başlamadı]\n[İPTAL EDİLDİ]: x",
                WingetInstall.LookupStoppedOutput("[İPTAL EDİLDİ]: x"));
        }

        // Kurulduğu yer sürüm satırından sonra (sürüm satırı yoksa kapsamdan hemen sonra) gelir
        [Fact]
        public void ResultOutput_PutsTheLocationAfterTheVersion()
        {
            Assert.Equal("[kapsam: machine]\n[sürüm: kaynaktaki en yeni sürüm 2.1.65 seçildi]\n"
                + "[kurulduğu yer: yalnızca SYSTEM hesabının profili — bilgisayarın kullanıcıları göremez]\nSuccessfully installed",
                WingetInstall.ResultOutput("Successfully installed", null, WingetInstall.PinnedNote("2.1.65"), WingetInstall.SystemProfileNote));
            Assert.Equal("[kapsam: machine]\n[kurulduğu yer: makine geneli]\nx", WingetInstall.ResultOutput("x", null, null, WingetInstall.MachineWideNote));
            Assert.Equal("[kapsam: user — machine yükleyici yok, kapsamsız yeniden denendi]\n[kurulduğu yer: belirlenemedi (yeni ya da sürümü değişen kaldırma kaydı yok)]\n"
                + "[1. deneme, --scope machine: çıkış -1978335216]\na\n[2. deneme, kapsamsız]\nb",
                WingetInstall.ResultOutput("b", "a", null, WingetInstall.UnknownLocationNote(WingetLocation.NothingNewReason)));
        }

        // Kurulduğu yer yalnızca paket bilgisayardayken yazılır: kuruldu, yeniden başlatma bitirecek, zaten kuruluydu. Bilgisayarın
        // kendisini yeniden başlattığı kodda (0x8A15010B), hatalarda ve ajanın kendi kodlarında yazılmaz.
        [Theory]
        [InlineData(0, true)]
        [InlineData(-1978334967, true)]    // 0x8A150109 yeniden başlatma bitirecek
        [InlineData(-1978335189, true)]    // 0x8A15002B zaten kurulu
        [InlineData(-1978335135, true)]    // 0x8A150061 zaten kurulu
        [InlineData(-1978334965, false)]   // 0x8A15010B bilgisayar yeniden başlıyor
        [InlineData(-1978335216, false)]   // 0x8A150010 uygulanabilir yükleyici yok
        [InlineData(-1978335212, false)]   // 0x8A150014 paket yok
        [InlineData(1, false)]
        [InlineData(-1, false)]
        [InlineData(-2, false)]
        [InlineData(-4, false)]
        public void IsOnThePc_OnlyAfterAnInstalledPackage(int exitCode, bool onThePc) => Assert.Equal(onThePc, WingetInstall.IsOnThePc(exitCode));

        // Sürüm sorgusu: yalnızca topluluk kaynağı, tam eşleşme, sürüm listesi; hiçbir şey kurmaz
        [Fact]
        public void VersionsArguments_AreTheAgreedCommandLine() =>
            Assert.Equal(new[] { "show", "--id", "Anki.Anki", "-e", "--source", "winget", "--versions", "--accept-source-agreements", "--disable-interactivity" },
                WingetInstall.VersionsArguments("Anki.Anki"));

        // winget'in tablosu: başlık (yerelleştirilir), tire satırı, en yeni sürüm başta. İmleç ve satır başı (\r) çizimleri,
        // sondaki dolgu boşlukları sonucu değiştirmez.
        [Theory]
        [InlineData("Found Anki [Anki.Anki]\r\nVersion\r\n-------\r\n26.09.3\r\n25.02.7\r\n2.1.66\r\n2.1.65\r\n", "26.09.3")]
        [InlineData("   -\r   \\\r   |\r        \rFound Anki [Anki.Anki]\r\nVersion\r\n-------\r\n26.09.3   \r\n25.02.7   \r\n", "26.09.3")]
        [InlineData("Anki [Anki.Anki] bulundu\nSürüm\n-----\n\n26.09.3\n", "26.09.3")]
        [InlineData("Version\n----------------\n14.40.33810.0\n", "14.40.33810.0")]
        [InlineData("Version\n-------\n1.0.0-beta+2\n", "1.0.0-beta+2")]
        public void LatestVersion_IsTheFirstRowOfTheTable(string output, string latest)
        {
            Assert.Equal(latest, WingetInstall.LatestVersion(output, out string reason));
            Assert.Equal("", reason);
        }

        // Bulunamayan, kısaltılmış ("…"), boşluklu ya da sunucu kuralına uymayan sürüm kullanılmaz
        [Theory]
        [InlineData("", "çıktıda sürüm listesi yok")]
        [InlineData("Found Anki [Anki.Anki]\n26.09.3\n", "çıktıda sürüm listesi yok")]
        [InlineData("   -\r   \\\r\n26.09.3\n", "çıktıda sürüm listesi yok")]
        [InlineData("Version\n-------\n", "sürüm listesi boş")]
        [InlineData("Version\n-------\n26.09.…\n25.02.7\n", "listedeki ilk sürüm geçersiz ya da kısaltılmış")]
        [InlineData("Version\n-------\n26 09 3\n", "listedeki ilk sürüm geçersiz ya da kısaltılmış")]
        [InlineData("Version\n-------\n1|calc\n", "listedeki ilk sürüm geçersiz ya da kısaltılmış")]
        [InlineData("Version\n-------\n12345678901234567890123456789012345678901\n", "listedeki ilk sürüm geçersiz ya da kısaltılmış")]
        public void LatestVersion_RefusesWhatItCannotTrust(string output, string expected)
        {
            Assert.Null(WingetInstall.LatestVersion(output, out string reason));
            Assert.Equal(expected, reason);
        }

        [Fact]
        public void QueryFailure_NamesTheCodeOrTheLimit()
        {
            Assert.Null(WingetInstall.QueryFailure("show", 0, false, TimeSpan.FromSeconds(60)));
            Assert.Equal("winget show çıkış kodu -1978335212", WingetInstall.QueryFailure("show", -1978335212, false, TimeSpan.FromSeconds(60)));
            Assert.Equal("winget show 60 sn içinde bitmedi", WingetInstall.QueryFailure("show", CommandRunner.ExitTimeout, true, TimeSpan.FromSeconds(60)));
            Assert.Equal(TimeSpan.FromSeconds(60), WingetInstall.QueryLimit);
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
        // winget'in işaretli 32 bit kodu olarak aynen gider. 0x8A15010B'de (bilgisayar yeniden başlıyor) kurulduğu yer sorulmaz:
        // tek çağrı, kurulduğu yer satırı yok
        [Fact]
        public async Task Install_RunsTheProgramWithTheAgreedArguments_AndReportsItsExitCode()
        {
            string fake = Path.Combine(TestEnvironment.NewDir("fake-winget"), "winget.cmd");
            File.WriteAllText(fake, "@echo off\r\necho ARGS:%*\r\nexit /b -1978334965\r\n");
            _locator = () => fake;
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(46, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            JsonElement result = await ResultFor(46);
            Assert.Equal(-1978334965, result.GetProperty("exit_code").GetInt32());
            Assert.Equal("[kapsam: machine]\nARGS:install --id Mozilla.Firefox -e --source winget --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity --version 131.0",
                result.GetProperty("output").GetString());
            Assert.Empty(Sent("capability_denied"));
        }

        // Kaynaktaki sürümler (winget show --versions'ın çıktısı gibi; en yenisi başta)
        private const string Versions = "Found Mozilla Firefox [Mozilla.Firefox]\r\nVersion\r\n-------\r\n131.0.3\r\n131.0.2\r\n131.0\r\n";

        // Sahte winget: her çağrının bağımsız değişkenlerini calls.txt'ye yazar. "show" (sürüm sorgusu): show.txt'yi yazar, showExit
        // ile biter (showPing: önce o kadar ping, yaklaşık saniye). Kurulum: bağımsız değişkenleri çıktıya yazar; --scope ile
        // çağrılırsa scoped, yoksa unscoped koduyla biter (unscopedPing: önce o kadar ping). Başka bir komut (ör. list) 1 ile biter.
        private (string Fake, string Calls) FakeWinget(int scoped, int unscoped, int unscopedPing = 0, string show = Versions, int showExit = 0, int showPing = 0)
        {
            string dir = TestEnvironment.NewDir("fake-winget");
            string fake = Path.Combine(dir, "winget.cmd");
            string calls = Path.Combine(dir, "calls.txt");
            File.WriteAllText(Path.Combine(dir, "show.txt"), show);
            File.WriteAllText(fake, "@echo off\r\n>>\"%~dp0calls.txt\" echo %*\r\nif \"%1\"==\"show\" goto show\r\nif not \"%1\"==\"install\" exit /b 1\r\n"
                + "echo ARGS:%*\r\nset scoped=0\r\n"
                + "for %%a in (%*) do if \"%%a\"==\"--scope\" set scoped=1\r\n"
                + $"if %scoped%==1 exit /b {scoped}\r\n"
                + (unscopedPing > 0 ? $"ping -n {unscopedPing} 127.0.0.1 > nul\r\n" : "")
                + $"exit /b {unscoped}\r\n"
                + ":show\r\n"
                + (showPing > 0 ? $"ping -n {showPing} 127.0.0.1 > nul\r\n" : "")
                + "type \"%~dp0show.txt\"\r\n"
                + $"exit /b {showExit}\r\n");
            _locator = () => fake;
            _calls = calls;
            return (fake, calls);
        }

        private static string[] Calls(string calls)
        {
            try { return File.Exists(calls) ? File.ReadAllLines(calls).Select(l => l.Trim()).ToArray() : Array.Empty<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
        }

        private const string ShowArgs = "show --id Mozilla.Firefox -e --source winget --versions --accept-source-agreements --disable-interactivity";
        private const string MachineArgs = "install --id Mozilla.Firefox -e --source winget --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity";
        private const string UnscopedArgs = "install --id Mozilla.Firefox -e --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
        private const string Pinned = " --version 131.0.3";
        private const string PinnedLine = "[sürüm: kaynaktaki en yeni sürüm 131.0.3 seçildi]";
        // Sahte kayıt defteri verilmedi: kurulumdan sonra da yeni kayıt yok
        private const string NothingNew = "[kurulduğu yer: belirlenemedi (yeni ya da sürümü değişen kaldırma kaydı yok)]";

        private WingetInstallHandler Handler(Worker worker) => (WingetInstallHandler)worker.Dispatcher.HandlerFor(WingetInstall.Action);

        // Yalnızca kullanıcı kapsamında yükleyicisi olan paket: aynı görevde bir kez --scope olmadan; tek sonuç, ikinci denemenin
        // çıkış kodu, kapsam ilk satırda, iki denemenin çıktısı sırayla. Sürümsüz emirde iki deneme de sabitlenen sürümü taşır.
        // SYSTEM olarak kapsamsız kurulan kullanıcı MSI'ı SYSTEM'in profilindedir: kurulduğu yer bunu söyler. Kayıt defteri sürüm
        // sorgusundan sonra, ilk denemeden önce ve son denemeden sonra okunur.
        [Fact]
        public async Task NoMachineInstaller_IsRetriedOnceWithoutScope_AndReportsTheSecondAttempt()
        {
            (_, string calls) = FakeWinget(WingetInstall.ExitNoApplicableInstaller, -1978334967);
            Registry(WingetLocationTests.Before, () => WingetLocationTests.Anki26After(), WingetLocationTests.Before, () => WingetLocationTests.Anki26After());
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(48, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(48);
            Assert.Equal(-1978334967, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"{WingetInstall.UserScopeNote}\n{PinnedLine}\n{WingetInstall.SystemProfileNote}\n[1. deneme, --scope machine: çıkış -1978335216]\n"
                + $"ARGS:{MachineArgs}{Pinned}\n[2. deneme, kapsamsız]\nARGS:{UnscopedArgs}{Pinned}", result.GetProperty("output").GetString());
            Assert.Equal(new[] { ShowArgs, MachineArgs + Pinned, UnscopedArgs + Pinned }, Calls(calls));
            Assert.Equal(new[] { 1, 3 }, RegistryReads());

            // Sürümlü emirde sorgu yok, iki deneme de sunucunun sürümünü taşır, sürüm satırı yok
            await worker.HandleServerMessageAsync(Install(49, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            JsonElement versioned = await ResultFor(49);
            Assert.Equal(new[] { ShowArgs, MachineArgs + Pinned, UnscopedArgs + Pinned, MachineArgs + " --version 131.0", UnscopedArgs + " --version 131.0" }, Calls(calls));
            Assert.StartsWith(WingetInstall.UserScopeNote + "\n" + WingetInstall.SystemProfileNote + "\n[1. deneme", versioned.GetProperty("output").GetString());
            Assert.Equal(new[] { 1, 3, 3, 5 }, RegistryReads());
            await Task.Delay(200);
            Assert.Equal(1, Sent("result").Count(m => m.GetProperty("task_id").GetInt32() == 48));
            Assert.Equal(1, Sent("result").Count(m => m.GetProperty("task_id").GetInt32() == 49));
        }

        // Kapsamsız deneme de "uygulanabilir yükleyici yok" ile biterse üçüncü deneme yok: o kod bildirilir. Paket kurulmadı:
        // kurulumdan sonraki kayıtlar okunmaz, kurulduğu yer satırı yok
        [Fact]
        public async Task NoInstallerAtAll_IsRetriedOnlyOnce()
        {
            (_, string calls) = FakeWinget(WingetInstall.ExitNoApplicableInstaller, WingetInstall.ExitNoApplicableInstaller);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(50, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(50);
            Assert.Equal(-1978335216, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith(WingetInstall.UserScopeNote + "\n" + PinnedLine + "\n[1. deneme", result.GetProperty("output").GetString());
            Assert.Equal(new[] { ShowArgs, MachineArgs + Pinned, UnscopedArgs + Pinned }, Calls(calls));
            Assert.Equal(new[] { 1 }, RegistryReads());
        }

        // Başka bir kod yeniden denenmez: tek kurulum çağrısı, kapsam machine. Kurulduğu yer yalnızca başarıda yazılır (burada
        // makine geneli bir MSI).
        [Theory]
        [InlineData(-1978335212)]
        [InlineData(0)]
        public async Task OtherExitCodes_RunOnce_WithMachineScope(int exitCode)
        {
            (_, string calls) = FakeWinget(exitCode, 0);
            Registry(WingetLocationTests.Before, WingetLocationTests.SevenZipAfter);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(51, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(51);
            Assert.Equal(exitCode, result.GetProperty("exit_code").GetInt32());
            Assert.Equal(new[] { ShowArgs, MachineArgs + Pinned }, Calls(calls));
            if (exitCode == 0)
            {
                Assert.Equal($"[kapsam: machine]\n{PinnedLine}\n[kurulduğu yer: makine geneli]\nARGS:{MachineArgs}{Pinned}", result.GetProperty("output").GetString());
                Assert.Equal(new[] { 1, 2 }, RegistryReads());
            }
            else
            {
                Assert.Equal($"[kapsam: machine]\n{PinnedLine}\nARGS:{MachineArgs}{Pinned}", result.GetProperty("output").GetString());
                Assert.Equal(new[] { 1 }, RegistryReads());
            }
        }

        // Kapsamsız deneme aynı görevdir: sürerken gelen aynı emir yeni bir kurulum başlatmaz, cancel_task onu sonlandırır, tek sonuç
        [Fact]
        public async Task Retry_IsTheSameTask_ForDuplicatesAndCancel()
        {
            (_, string calls) = FakeWinget(WingetInstall.ExitNoApplicableInstaller, 0, unscopedPing: 30);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(52, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            for (int i = 0; i < 200 && Calls(calls).Length < 2; i++) await Task.Delay(50);
            Assert.Equal(new[] { MachineArgs + " --version 131.0", UnscopedArgs + " --version 131.0" }, Calls(calls));

            await worker.HandleServerMessageAsync(Install(52, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
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
            Assert.Equal(new[] { 0 }, RegistryReads());
        }

        // Saha testi (2026-10-22): sürümsüz ve --scope machine ile winget, machine yükleyicisi olan en yeni sürümü sessizce seçti
        // (Anki.Anki 26.09.3 yerine 2.1.65). Önce kaynaktaki en yeni sürüm sorulur, kurulum ona sabitlenir; o sürümün machine
        // yükleyicisi yoksa ilk deneme 0x8A150010 ile biter ve aynı sürümle kapsamsız yeniden denenir. Saha testinin ikinci
        // bulgusu: kapsamsız kurulan 26.09.3'ün kaldırma kaydı HKLM'de olduğu için 0.1.24-alpha (winget list --scope machine)
        // "makine geneli" yazdı; ürün SYSTEM'e kullanıcı başına kayıtlı, dosyalar SYSTEM'in profilinde. winget list artık
        // çağrılmaz, satır kayıt defterinden SYSTEM'in profilini söyler.
        [Fact]
        public async Task NoVersion_PinsTheLatest_AndRetriesTheSameVersionWithoutScope()
        {
            (_, string calls) = FakeWinget(WingetInstall.ExitNoApplicableInstaller, 0,
                show: "Found Anki [Anki.Anki]\r\nVersion\r\n-------\r\n26.09.3\r\n25.02.7\r\n2.1.66\r\n2.1.65\r\n");
            Registry(WingetLocationTests.Before, () => WingetLocationTests.Anki26After());
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(60, "Anki.Anki"), null, CancellationToken.None);
            JsonElement result = await ResultFor(60);
            string install = "install --id Anki.Anki -e --source winget --silent";
            string tail = " --accept-package-agreements --accept-source-agreements --disable-interactivity --version 26.09.3";
            Assert.Equal(new[] { "show --id Anki.Anki -e --source winget --versions --accept-source-agreements --disable-interactivity",
                install + " --scope machine" + tail, install + tail }, Calls(calls));
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"{WingetInstall.UserScopeNote}\n[sürüm: kaynaktaki en yeni sürüm 26.09.3 seçildi]\n{WingetInstall.SystemProfileNote}\n"
                + $"[1. deneme, --scope machine: çıkış -1978335216]\nARGS:{install} --scope machine{tail}\n[2. deneme, kapsamsız]\nARGS:{install}{tail}",
                result.GetProperty("output").GetString());
            Assert.Equal(new[] { 1, 3 }, RegistryReads());
        }

        // En yeni sürüm bulunamazsa bugünkü gibi sürüm verilmeden kurulur; çıktının ikinci satırı nedenini söyler
        [Theory]
        [InlineData(Versions, -1978335212, "winget show çıkış kodu -1978335212")]
        [InlineData("No package found matching input criteria.\r\n", -1978335212, "winget show çıkış kodu -1978335212")]
        [InlineData("Found Mozilla Firefox [Mozilla.Firefox]\r\nsomething else\r\n", 0, "çıktıda sürüm listesi yok")]
        [InlineData("Version\r\n-------\r\n131.0.…\r\n131.0.2\r\n", 0, "listedeki ilk sürüm geçersiz ya da kısaltılmış")]
        [InlineData("Version\r\n-------\r\n131 0 3\r\n", 0, "listedeki ilk sürüm geçersiz ya da kısaltılmış")]
        [InlineData("Version\r\n-------\r\n", 0, "sürüm listesi boş")]
        public async Task LatestUnknown_InstallsWithoutAVersion_AndSaysWhy(string show, int showExit, string reason)
        {
            (_, string calls) = FakeWinget(0, 0, show: show, showExit: showExit);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(61, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(61);
            Assert.Equal(new[] { ShowArgs, MachineArgs }, Calls(calls));
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"[kapsam: machine]\n[sürüm: en yeni sürüm bulunamadı ({reason}); winget kendisi seçti]\n{NothingNew}\nARGS:{MachineArgs}",
                result.GetProperty("output").GetString());
        }

        // Sorgu kendi süre sınırında bitmezse sonlandırılır ve sürüm verilmeden kurulur; görev sürer
        [Fact]
        public async Task SlowLookup_IsEnded_AndInstallsWithoutAVersion()
        {
            (_, string calls) = FakeWinget(0, 0, showPing: 30);
            using Worker worker = NewWorker();
            Handler(worker).QueryLimit = TimeSpan.FromSeconds(1);
            await worker.HandleServerMessageAsync(Install(62, "Mozilla.Firefox"), null, CancellationToken.None);
            JsonElement result = await ResultFor(62);
            Assert.Equal(new[] { ShowArgs, MachineArgs }, Calls(calls));
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"[kapsam: machine]\n[sürüm: en yeni sürüm bulunamadı (winget show 1 sn içinde bitmedi); winget kendisi seçti]\n"
                + $"{NothingNew}\nARGS:{MachineArgs}", result.GetProperty("output").GetString());
            using Worker fresh = NewWorker();
            Assert.Equal(WingetInstall.QueryLimit, Handler(fresh).QueryLimit);
        }

        // Sürüm sorgusu da aynı görevdir: sürerken gelen aynı emir yeni bir şey başlatmaz, cancel_task onu sonlandırır, kurulum
        // başlamaz (kayıt defteri de okunmaz); tek sonuç, kapsam satırı yok
        [Fact]
        public async Task Lookup_IsTheSameTask_ForDuplicatesAndCancel()
        {
            (_, string calls) = FakeWinget(0, 0, showPing: 30);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(63, "Mozilla.Firefox"), null, CancellationToken.None);
            for (int i = 0; i < 200 && Calls(calls).Length < 1; i++) await Task.Delay(50);
            Assert.Equal(new[] { ShowArgs }, Calls(calls));

            await worker.HandleServerMessageAsync(Install(63, "Mozilla.Firefox"), null, CancellationToken.None);
            await Task.Delay(300);
            Assert.Single(Calls(calls));

            await worker.HandleServerMessageAsync("{\"action\":\"cancel_task\",\"task_id\":63}", null, CancellationToken.None);
            JsonElement result = await ResultFor(63);
            Assert.Equal(CommandRunner.ExitCancelled, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith(WingetInstall.LookupStoppedNote + "\n[İPTAL EDİLDİ]", result.GetProperty("output").GetString());
            await Task.Delay(300);
            Assert.Equal(new[] { ShowArgs }, Calls(calls));
            Assert.Equal(1, Sent("result").Count(m => m.GetProperty("task_id").GetInt32() == 63));
            Assert.Empty(RegistryReads());
        }

        // Servis sorgu sırasında durursa sorgu sonlandırılır, kurulum başlamaz (-4)
        [Fact]
        public async Task ServiceStopDuringLookup_StartsNoInstall()
        {
            (_, string calls) = FakeWinget(0, 0, showPing: 30);
            using var stopping = new CancellationTokenSource();
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(64, "Mozilla.Firefox"), null, stopping.Token);
            for (int i = 0; i < 200 && Calls(calls).Length < 1; i++) await Task.Delay(50);
            stopping.Cancel();
            JsonElement result = await ResultFor(64);
            Assert.Equal(CommandRunner.ExitServiceStopping, result.GetProperty("exit_code").GetInt32());
            Assert.StartsWith(WingetInstall.LookupStoppedNote + "\n[DURDURULDU]", result.GetProperty("output").GetString());
            await Task.Delay(300);
            Assert.Equal(new[] { ShowArgs }, Calls(calls));
            Assert.Empty(RegistryReads());
        }

        // Saha testi (2026-10-22): manifestinde "Scope: machine" yazan Anki 2.1.65'in 32 bit NSIS yükleyicisi SYSTEM olarak SYSTEM'in
        // kendi profiline kurdu (kaldırma kaydı HKU\S-1-5-18'de). Kapsam satırı machine kalır, kurulduğu yer satırı paketin nerede
        // olduğunu söyler.
        [Fact]
        public async Task MachineRun_ThatLandedInSystemsProfile_SaysSo()
        {
            (_, string calls) = FakeWinget(0, 0);
            Registry(WingetLocationTests.Before, WingetLocationTests.Anki2165After);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(70, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            JsonElement result = await ResultFor(70);
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"[kapsam: machine]\n[kurulduğu yer: yalnızca SYSTEM hesabının profili — bilgisayarın kullanıcıları göremez]\nARGS:{MachineArgs} --version 131.0",
                result.GetProperty("output").GetString());
            Assert.Equal(new[] { MachineArgs + " --version 131.0" }, Calls(calls));
            Assert.Equal(new[] { 0, 1 }, RegistryReads());
        }

        // Kurulduğu yer kurulumdan önceki ve sonraki kayıtlardan: makine geneli MSI, makine geneli taşınabilir paket, yeni kayıt
        // yok. Yeniden başlatmanın bitireceği kurulumda (0x8A150109) da okunur.
        [Theory]
        [InlineData("7zip", 0, "[kurulduğu yer: makine geneli]")]
        [InlineData("telegram", 0, "[kurulduğu yer: makine geneli]")]
        [InlineData("nothing", 0, NothingNew)]
        [InlineData("anki", -1978334967, "[kurulduğu yer: yalnızca SYSTEM hesabının profili — bilgisayarın kullanıcıları göremez]")]
        public async Task Location_ComesFromTheRegistryBeforeAndAfterTheInstall(string after, int exitCode, string note)
        {
            (_, string calls) = FakeWinget(exitCode, 0);
            Registry(WingetLocationTests.Before, after switch
            {
                "7zip" => WingetLocationTests.SevenZipAfter,
                "telegram" => WingetLocationTests.TelegramAfter,
                "anki" => () => WingetLocationTests.Anki26After(),
                _ => WingetLocationTests.Before,
            });
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(71, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            JsonElement result = await ResultFor(71);
            Assert.Equal(exitCode, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"[kapsam: machine]\n{note}\nARGS:{MachineArgs} --version 131.0", result.GetProperty("output").GetString());
            Assert.Equal(new[] { MachineArgs + " --version 131.0" }, Calls(calls));
            Assert.Equal(new[] { 0, 1 }, RegistryReads());
        }

        // Paket zaten kuruluydu: yükleyici çalışmadı, kurulumdan sonraki kayıtlar okunmaz, arada değişen kayıt pakete ait sayılmaz
        [Theory]
        [InlineData(-1978335189)]   // 0x8A15002B
        [InlineData(-1978335135)]   // 0x8A150061
        public async Task AlreadyInstalled_SaysTheLocationIsUnknown(int exitCode)
        {
            (_, string calls) = FakeWinget(exitCode, 0);
            Registry(WingetLocationTests.Before, WingetLocationTests.SevenZipAfter);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(72, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            JsonElement result = await ResultFor(72);
            Assert.Equal(exitCode, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"[kapsam: machine]\n[kurulduğu yer: belirlenemedi (paket zaten kuruluydu; yükleyici çalışmadı)]\nARGS:{MachineArgs} --version 131.0",
                result.GetProperty("output").GetString());
            Assert.Equal(new[] { 0 }, RegistryReads());
        }

        // Kayıt defteri okunamazsa (kapı hata atar) kurulduğu yer belirlenemedi; görev ve sonucu kurulumundur
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task UnreadableRegistry_SaysTheLocationIsUnknown(bool beforeFails)
        {
            (_, string calls) = FakeWinget(0, 0);
            Func<InstallRegistrySnapshot> fails = () => throw new UnauthorizedAccessException("erişim reddedildi");
            Registry(beforeFails ? fails : WingetLocationTests.Before, beforeFails ? () => WingetLocationTests.Anki26After() : fails);
            using Worker worker = NewWorker();
            await worker.HandleServerMessageAsync(Install(73, "Mozilla.Firefox", "131.0"), null, CancellationToken.None);
            JsonElement result = await ResultFor(73);
            Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
            Assert.Equal($"[kapsam: machine]\n[kurulduğu yer: belirlenemedi (kayıt defteri okunamadı)]\nARGS:{MachineArgs} --version 131.0",
                result.GetProperty("output").GetString());
            Assert.Equal(new[] { MachineArgs + " --version 131.0" }, Calls(calls));
            await Task.Delay(200);
            Assert.Equal(1, Sent("result").Count(m => m.GetProperty("task_id").GetInt32() == 73));
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
