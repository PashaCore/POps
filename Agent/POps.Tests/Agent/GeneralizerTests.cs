using System;
using System.IO;
using System.Linq;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // POpsAgent.exe --generalize [--enroll-token <jeton>]
    [Collection(SharedStateCollection.Name)]
    public class GeneralizerTests : SharedStateTestBase, IDisposable
    {
        private const string Token = "multi-use-token-0123456789abcdef";
        private readonly Func<bool> _isAdmin = Generalizer.IsAdministrator;
        private readonly Func<string> _stop = Generalizer.StopAgent;
        private int _stops;

        public GeneralizerTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("gen-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("gen-data");
            POpsHelpers.ConfigPaths = new string[0];
            Generalizer.IsAdministrator = () => true;
            Generalizer.StopAgent = () => { _stops++; return null; };
        }

        public void Dispose()
        {
            Generalizer.IsAdministrator = _isAdmin;
            Generalizer.StopAgent = _stop;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
        }

        private static string[] DeviceFiles => new[]
        {
            AgentUpdate.IdentityPath,
            SecureStore.PathOf(AgentCredentials.SecretFileName),
            SecureStore.PathOf(AgentCredentials.DeviceBypassSecretFileName),
            SecureStore.PathOf(HardwareBinding.FileName),
            SecureStore.PathOf(ResultSpool.FileName),
            AgentUpdate.ResultPath,
            AgentUpdate.ReportedResultPath,
            Path.Combine(AgentUpdate.DataDir, SoftwareReporter.StateFileName),
        };

        // Bilgisayara değil kuruma ait olanlar: kalır
        private static string[] SharedFiles => new[]
        {
            SecureStore.PathOf(AgentCredentials.BypassSecretFileName),
            SecureStore.PathOf(ServerTrust.FileName),
            Path.Combine(AgentUpdate.DataDir, AgentCapabilities.FileName),
        };

        private static string CloneFolder => SecureStore.PathOf("clone-20260101-000000");

        private void Prepare()
        {
            foreach (string path in DeviceFiles.Concat(SharedFiles)) File.WriteAllText(path, "x");
            Directory.CreateDirectory(CloneFolder);
            File.WriteAllText(Path.Combine(CloneFolder, AgentCredentials.SecretFileName), "old");
        }

        private static (int Code, string Output) Run(params string[] args)
        {
            var output = new StringWriter();
            int code = Generalizer.Run(args, output, AgentHarness.FromStatics().Paths);
            return (code, output.ToString());
        }

        private static void AssertUntouched()
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
            Assert.Equal(Token, SecureStore.Read(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName)));
            Assert.Contains("Silindi: " + AgentUpdate.IdentityPath, output);
        }

        [Fact]
        public void Generalize_AlsoDeletesThePersistDirCopies()
        {
            string thaw = TestEnvironment.NewDir("gen-thaw");
            string config = Path.Combine(TestEnvironment.NewDir("gen-cfg"), "appsettings.json");
            File.WriteAllText(config, "{\"PersistDir\":\"" + thaw.Replace("\\", "\\\\") + "\"}");
            POpsHelpers.ConfigPaths = new[] { config };
            File.WriteAllText(Path.Combine(thaw, AgentCredentials.SecretFileName), "x");
            File.WriteAllText(Path.Combine(thaw, HardwareBinding.FileName), "x");

            Assert.Equal(Generalizer.ExitOk, Run("--generalize").Code);
            Assert.False(File.Exists(Path.Combine(thaw, AgentCredentials.SecretFileName)));
            Assert.False(File.Exists(Path.Combine(thaw, HardwareBinding.FileName)));
            POpsHelpers.ConfigPaths = new string[0];
        }

        [Fact]
        public void Generalize_WithoutToken_KeepsAnExistingToken()
        {
            Prepare();
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName), Token);
            Assert.Equal(Generalizer.ExitOk, Run("--generalize").Code);
            Assert.Equal(Token, SecureStore.Read(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName)));
        }

        [Fact]
        public void NotAdministrator_Exits2_AndTouchesNothing()
        {
            Prepare();
            Generalizer.IsAdministrator = () => false;
            Assert.Equal(Generalizer.ExitNotAdmin, Run("--generalize", "--enroll-token", Token).Code);
            Assert.Equal(0, _stops);
            AssertUntouched();
            Assert.False(File.Exists(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName)));
        }

        [Fact]
        public void ServiceNotStopped_Exits3_AndTouchesNothing()
        {
            Prepare();
            Generalizer.StopAgent = () => "zaman aşımı";
            var (code, output) = Run("--generalize", "--enroll-token", Token);
            Assert.Equal(Generalizer.ExitNotStopped, code);
            Assert.Contains("zaman aşımı", output);
            AssertUntouched();
            Assert.False(File.Exists(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName)));
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
