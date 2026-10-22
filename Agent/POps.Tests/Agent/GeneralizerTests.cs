using System;
using System.IO;
using System.Linq;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // POpsAgent.exe --generalize [--enroll-token <jeton>]
    public class GeneralizerTests : TestBase, IDisposable
    {
        private const string Token = "multi-use-token-0123456789abcdef";
        // İşletim sistemi sınırları (makine kapıları): yönetici mi, servis ve watchdog durduruldu mu
        private Func<bool> _isAdmin = () => true;
        private Func<string> _stop;
        private int _stops;
        // Servisin klasörleri; ayar dosyası (PersistDir) baştan bağlamın ayar dosyasıdır, yazılana kadar yoktur
        private readonly string _config = Path.Combine(TestEnvironment.NewDir("gen-cfg"), "appsettings.json");
        private readonly AgentHarness _agent;

        public GeneralizerTests()
        {
            _stop = () => { _stops++; return null; };
            _agent = AgentHarness.Create("gen", configPaths: new[] { _config },
                machine: new MachineSeams { Kiosk = new FakeKioskRegistry(), IsAdministrator = () => _isAdmin(), StopAgent = () => _stop() });
        }

        public void Dispose() => _agent.Dispose();

        private string[] DeviceFiles => new[]
        {
            _agent.Paths.IdentityPath,
            _agent.Paths.SecureFile(AgentCredentials.SecretFileName),
            _agent.Paths.SecureFile(AgentCredentials.DeviceBypassSecretFileName),
            _agent.Paths.SecureFile(HardwareBinding.FileName),
            _agent.Paths.SecureFile(ResultSpool.FileName),
            _agent.Paths.UpdateResultPath,
            _agent.Paths.ReportedResultPath,
            Path.Combine(_agent.Paths.DataDir, SoftwareReporter.StateFileName),
        };

        // Bilgisayara değil kuruma ait olanlar: kalır
        private string[] SharedFiles => new[]
        {
            _agent.Paths.SecureFile(AgentCredentials.BypassSecretFileName),
            _agent.Paths.SecureFile(ServerTrust.FileName),
            Path.Combine(_agent.Paths.DataDir, AgentCapabilities.FileName),
        };

        private string CloneFolder => _agent.Paths.SecureFile("clone-20260101-000000");

        private void Prepare()
        {
            foreach (string path in DeviceFiles.Concat(SharedFiles)) File.WriteAllText(path, "x");
            Directory.CreateDirectory(CloneFolder);
            File.WriteAllText(Path.Combine(CloneFolder, AgentCredentials.SecretFileName), "old");
        }

        private (int Code, string Output) Run(params string[] args)
        {
            var output = new StringWriter();
            int code = Generalizer.Run(args, output, _agent.Paths, _agent.Machine.IsAdministrator, _agent.Machine.StopAgent);
            return (code, output.ToString());
        }

        private void AssertUntouched()
        {
            foreach (string path in DeviceFiles.Concat(SharedFiles)) Assert.True(File.Exists(path), path);
            Assert.True(Directory.Exists(CloneFolder));
        }

        [Fact]
        public void IsRequested_OnlyWithTheSwitch()
        {
            Assert.True(Generalizer.IsRequested(new[] { "--GENERALIZE" }));
            Assert.False(Generalizer.IsRequested(new[] { "POpsV" }));
            Assert.False(Generalizer.IsRequested(null));
        }

        [Fact]
        public void Generalize_DeletesDeviceFiles_AndWritesTheToken()
        {
            Prepare();
            var (code, output) = Run("--generalize", "--enroll-token", Token);
            Assert.Equal(Generalizer.ExitOk, code);
            Assert.Equal(1, _stops);
            foreach (string path in DeviceFiles) Assert.False(File.Exists(path), path);
            Assert.False(Directory.Exists(CloneFolder));
            foreach (string path in SharedFiles) Assert.True(File.Exists(path), path);
            Assert.Equal(Token, SecureStore.Read(_agent.Paths.SecureFile(AgentCredentials.EnrollTokenFileName)));
            Assert.Contains("Silindi: " + _agent.Paths.IdentityPath, output);
        }

        [Fact]
        public void Generalize_AlsoDeletesThePersistDirCopies()
        {
            string thaw = TestEnvironment.NewDir("gen-thaw");
            File.WriteAllText(_config, "{\"PersistDir\":\"" + thaw.Replace("\\", "\\\\") + "\"}");
            File.WriteAllText(Path.Combine(thaw, AgentCredentials.SecretFileName), "x");
            File.WriteAllText(Path.Combine(thaw, HardwareBinding.FileName), "x");

            Assert.Equal(Generalizer.ExitOk, Run("--generalize").Code);
            Assert.False(File.Exists(Path.Combine(thaw, AgentCredentials.SecretFileName)));
            Assert.False(File.Exists(Path.Combine(thaw, HardwareBinding.FileName)));
        }

        [Fact]
        public void Generalize_WithoutToken_KeepsAnExistingToken()
        {
            Prepare();
            SecureStore.WriteProtected(_agent.Paths.SecureFile(AgentCredentials.EnrollTokenFileName), Token);
            Assert.Equal(Generalizer.ExitOk, Run("--generalize").Code);
            Assert.Equal(Token, SecureStore.Read(_agent.Paths.SecureFile(AgentCredentials.EnrollTokenFileName)));
        }

        [Fact]
        public void NotAdministrator_Exits2_AndTouchesNothing()
        {
            Prepare();
            _isAdmin = () => false;
            Assert.Equal(Generalizer.ExitNotAdmin, Run("--generalize", "--enroll-token", Token).Code);
            Assert.Equal(0, _stops);
            AssertUntouched();
            Assert.False(File.Exists(_agent.Paths.SecureFile(AgentCredentials.EnrollTokenFileName)));
        }

        [Fact]
        public void ServiceNotStopped_Exits3_AndTouchesNothing()
        {
            Prepare();
            _stop = () => "zaman aşımı";
            var (code, output) = Run("--generalize", "--enroll-token", Token);
            Assert.Equal(Generalizer.ExitNotStopped, code);
            Assert.Contains("zaman aşımı", output);
            AssertUntouched();
            Assert.False(File.Exists(_agent.Paths.SecureFile(AgentCredentials.EnrollTokenFileName)));
        }

        [Theory]
        [InlineData("--generalize", "--enroll-token")]
        [InlineData("--generalize", "--enroll-token", "kısa")]
        [InlineData("--generalize", "--enroll-token", "token with spaces 0123456789")]
        [InlineData("--generalize", "--force")]
        public void BadUsage_Exits4_BeforeAnythingElse(params string[] args)
        {
            Prepare();
            Assert.Equal(Generalizer.ExitUsage, Run(args).Code);
            Assert.Equal(0, _stops);
            AssertUntouched();
        }
    }
}
