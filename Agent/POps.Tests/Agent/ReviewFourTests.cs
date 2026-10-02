using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // 4. inceleme, ajan: cihaz anahtarının donanıma bağı (hw.bind), kopyalanmış kurulum ve 4409
    public class HardwareBindingTests : TestBase, IDisposable
    {
        private const string Uuid = "4C4C4544-0042-3510-8052-B4C04F4B4E32";
        private const string Bios = "5B4KNK2";
        private const string OtherUuid = "4C4C4544-0047-3810-8033-C3C04F4B4E32";
        private const string OtherBios = "7C8LML3";
        private const string Secret = "binding-secret-0123456789abcdefghijkl";
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.Zero);

        public HardwareBindingTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("bind-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("bind-data");
            POpsHelpers.ConfigPaths = new string[0];
        }

        public void Dispose()
        {
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            POpsHelpers.ConfigPaths = new string[0];
        }

        private static HardwareBinding Binding(string uuid, string bios, DateTimeOffset? now = null) =>
            new HardwareBinding(AgentUpdate.IdentityPath, () => (uuid, bios), () => now ?? T0);

        // PersistDir (dondurulmayan klasör) ayarlanır
        private static string Persist()
        {
            string dir = TestEnvironment.NewDir("bind-thaw");
            string config = Path.Combine(TestEnvironment.NewDir("bind-cfg"), "appsettings.json");
            File.WriteAllText(config, "{\"PersistDir\":\"" + dir.Replace("\\", "\\\\") + "\"}");
            POpsHelpers.ConfigPaths = new[] { config };
            return dir;
        }

        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        // Aynı bilgisayarda kurulu ajanın dosyaları: kimlik, secret, cihaz bypass anahtarı, donanım bağı
        private static void Enrolled(string hwId = "HW-ORIGINAL")
        {
            File.WriteAllText(AgentUpdate.IdentityPath, hwId);
            Assert.True(AgentCredentials.SaveSecret(Secret, hwId));
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCredentials.DeviceBypassSecretFileName), "device-bypass");
            Assert.True(Binding(Uuid, Bios).Bind(hwId, "test"));
        }

        [Fact]
        public void Digest_UsesTheDnaPayloadNormalization()
        {
            Assert.Equal("NULL", HardwareBinding.NormalizeUuid("-"));
            Assert.Equal("NULL", HardwareBinding.NormalizeUuid("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"));
            Assert.Equal(Uuid, HardwareBinding.NormalizeUuid(Uuid));
            Assert.Equal("NULL", HardwareBinding.NormalizeBiosSerial("-"));
            Assert.Equal("NULL", HardwareBinding.NormalizeBiosSerial("To be filled by O.E.M."));
            Assert.Equal(Bios, HardwareBinding.NormalizeBiosSerial(Bios));
            Assert.Equal(Sha256(Uuid + "|" + Bios), HardwareBinding.Digest(Uuid, Bios));
            Assert.Equal(Sha256("NULL|NULL"), HardwareBinding.Digest(HardwareBinding.NormalizeUuid("-"), HardwareBinding.NormalizeBiosSerial("-")));
        }

        [Theory]
        [InlineData(Uuid, true)]
        [InlineData("NULL", false)]
        [InlineData("", false)]
        [InlineData("00000000-0000-0000-0000-000000000000", false)]
        [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", false)]
        [InlineData("03000200-0400-0500-0006-000700080009", false)]
        public void UuidReadability(string uuid, bool readable) => Assert.Equal(readable, HardwareBinding.IsReadableUuid(uuid));

        [Theory]
        [InlineData(Bios, true)]
        [InlineData("NULL", false)]
        [InlineData("   ", false)]
        [InlineData("Default string", false)]
        [InlineData("System Serial Number", false)]
        [InlineData("To Be Filled By O.E.M.", false)]
        [InlineData("00000000", false)]
        [InlineData("None", false)]
        public void SerialReadability(string serial, bool readable) => Assert.Equal(readable, HardwareBinding.IsReadableSerial(serial));

        [Fact]
        public void Evaluate_SameHardware_Matches()
        {
            var record = HardwareBinding.CreateRecord(Uuid, Bios, "HW-A", T0);
            Assert.Equal(BindingVerdict.Match, HardwareBinding.Evaluate(record, Uuid, Bios));
            Assert.Equal(BindingVerdict.Missing, HardwareBinding.Evaluate(null, Uuid, Bios));
        }

        [Theory]
        [InlineData(OtherUuid, OtherBios)]
        [InlineData(OtherUuid, Bios)]
        [InlineData(Uuid, OtherBios)]
        public void Evaluate_ReadableValueChanged_IsClone(string uuid, string bios) =>
            Assert.Equal(BindingVerdict.Clone, HardwareBinding.Evaluate(HardwareBinding.CreateRecord(Uuid, Bios, "HW-A", T0), uuid, bios));

        [Fact]
        public void Evaluate_UnreliableBiosAtBindTime_UuidStillDecides()
        {
            var record = HardwareBinding.CreateRecord(Uuid, "Default string", "HW-A", T0);
            Assert.Null(record.BiosSerial);
            Assert.Equal(BindingVerdict.Clone, HardwareBinding.Evaluate(record, OtherUuid, "Default string"));
            // Yalnızca güvenilmez parça değişti (ör. BIOS güncellemesi seri numarasını doldurdu): karar verilmez
            Assert.Equal(BindingVerdict.Inconclusive, HardwareBinding.Evaluate(record, Uuid, Bios));
        }

        [Theory]
        [InlineData("NULL", "NULL")]
        [InlineData("00000000-0000-0000-0000-000000000000", "Default string")]
        [InlineData("03000200-0400-0500-0006-000700080009", "0000000")]
        [InlineData(Uuid, "NULL")]
        [InlineData("NULL", Bios)]
        public void Evaluate_ChangedValueNotReadable_IsInconclusive(string uuid, string bios) =>
            Assert.Equal(BindingVerdict.Inconclusive, HardwareBinding.Evaluate(HardwareBinding.CreateRecord(Uuid, Bios, "HW-A", T0), uuid, bios));

        [Fact]
        public void Startup_Clone_MovesDeviceFilesAsideWithoutDeleting()
        {
            Enrolled();
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName), "enroll-token-0123456789abcdef");

            var binding = Binding(OtherUuid, OtherBios);
            Assert.Equal(BindingVerdict.Clone, binding.CheckOnStartup());

            string folder = SecureStore.PathOf("clone-20261002-093000");
            Assert.Equal(folder, binding.CloneFolder);
            Assert.Equal("HW-ORIGINAL", binding.PreviousHwId);
            Assert.Equal(new[] { "identity.key", "agent.secret", "bypass.device", "hw.bind" }, binding.MovedFiles);
            foreach (string name in binding.MovedFiles) Assert.True(File.Exists(Path.Combine(folder, name)), name);
            Assert.False(File.Exists(AgentUpdate.IdentityPath));
            Assert.False(File.Exists(SecureStore.PathOf(AgentCredentials.SecretFileName)));
            Assert.False(File.Exists(SecureStore.PathOf(AgentCredentials.DeviceBypassSecretFileName)));
            Assert.False(File.Exists(HardwareBinding.PrimaryPath));
            Assert.Equal("HW-ORIGINAL", File.ReadAllText(Path.Combine(folder, "identity.key")));
            // Jeton kalır: kopya onunla yeni cihaz olarak kaydolur
            Assert.True(File.Exists(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName)));
            Assert.Null(AgentCredentials.LoadSecret());
        }

        [Fact]
        public void Startup_Clone_AlsoMovesThePersistDirCopies()
        {
            string thaw = Persist();
            Enrolled();
            Assert.True(File.Exists(Path.Combine(thaw, AgentCredentials.SecretFileName)));
            Assert.True(File.Exists(Path.Combine(thaw, HardwareBinding.FileName)));

            var binding = Binding(OtherUuid, OtherBios);
            Assert.Equal(BindingVerdict.Clone, binding.CheckOnStartup());
            Assert.Contains("agent.secret.persist", binding.MovedFiles);
            Assert.Contains("hw.bind.persist", binding.MovedFiles);
            Assert.False(File.Exists(Path.Combine(thaw, AgentCredentials.SecretFileName)));
            Assert.Null(AgentCredentials.LoadSecret());
        }

        [Fact]
        public void Startup_Unreadable_TouchesNothing()
        {
            Enrolled();
            var binding = Binding("-", "To be filled by O.E.M.");
            Assert.Equal(BindingVerdict.Inconclusive, binding.CheckOnStartup());
            Assert.Null(binding.CloneFolder);
            Assert.True(File.Exists(AgentUpdate.IdentityPath));
            Assert.True(File.Exists(SecureStore.PathOf(AgentCredentials.SecretFileName)));
            Assert.True(File.Exists(HardwareBinding.PrimaryPath));
            Assert.Empty(Directory.GetDirectories(SecureStore.Dir, "clone-*"));
        }

        [Fact]
        public void Startup_SameHardware_Matches()
        {
            Enrolled();
            var binding = Binding(Uuid, Bios);
            Assert.Equal(BindingVerdict.Match, binding.CheckOnStartup());
            Assert.Equal("HW-ORIGINAL", binding.BoundHwId);
            Assert.True(File.Exists(SecureStore.PathOf(AgentCredentials.SecretFileName)));
        }

        // Dondurma yazılımı C:'yi imajdaki hâline döndürdü; dondurulmayan klasördeki (yeni) bağ geçerlidir
        [Fact]
        public void Startup_FrozenDisk_NewerPersistDirBindingWins()
        {
            Persist();
            Assert.True(Binding(Uuid, Bios).Bind("HW-CLONE", "test"));
            var original = HardwareBinding.CreateRecord(OtherUuid, OtherBios, "HW-ORIGINAL", T0.AddDays(-30));
            SecureStore.WriteProtected(HardwareBinding.PrimaryPath, JsonSerializer.Serialize(original));

            var binding = Binding(Uuid, Bios);
            Assert.Equal(BindingVerdict.Match, binding.CheckOnStartup());
            Assert.Equal("HW-CLONE", binding.BoundHwId);
        }

        [Fact]
        public void Bind_WithoutReadableHardware_WritesNothing()
        {
            Assert.False(Binding("-", "Default string").Bind("HW-A", "test"));
            Assert.False(File.Exists(HardwareBinding.PrimaryPath));
            Assert.Equal(BindingVerdict.Missing, Binding(Uuid, Bios).CheckOnStartup());
        }

        [Fact]
        public void UpdateHwId_KeepsTheHardware()
        {
            Assert.True(Binding(Uuid, Bios).Bind("HW-A", "test"));
            Binding(Uuid, Bios, T0.AddMinutes(1)).UpdateHwId("HW-B");
            BindingRecord record = HardwareBinding.Load();
            Assert.Equal("HW-B", record.HwId);
            Assert.Equal(HardwareBinding.Digest(Uuid, Bios), record.Digest);
        }

        private static Worker NewWorker(string hwId) => new Worker(NullLogger<Worker>.Instance)
        {
            HwId = hwId,
            SendOverride = _ => Task.FromResult(true),
            Binding = Binding(Uuid, Bios),
        };

        // 0.1.14 ve önceki kurulum: secret var, hw.bind yok; ilk açılışta bugünkü donanım yazılır
        [Fact]
        public void ExistingInstall_FirstUseTrust_WritesTheBinding()
        {
            Assert.True(AgentCredentials.SaveSecret(Secret, "HW-OLD"));
            using Worker worker = NewWorker("HW-OLD");
            Assert.Equal(BindingVerdict.Missing, worker.Binding.CheckOnStartup());
            worker.ApplyHardwareBinding(BindingVerdict.Missing);

            BindingRecord record = HardwareBinding.Load();
            Assert.Equal(HardwareBinding.Digest(Uuid, Bios), record.Digest);
            Assert.Equal("HW-OLD", record.HwId);
            Assert.Equal(BindingVerdict.Match, Binding(Uuid, Bios).CheckOnStartup());
        }

        [Fact]
        public async Task SetSecret_BindsTheKeyToThisHardware()
        {
            using Worker worker = NewWorker("HW-NEW");
            await worker.HandleServerMessageAsync("{\"action\":\"set_secret\",\"secret\":\"" + Secret + "\"}", null, CancellationToken.None);
            BindingRecord record = HardwareBinding.Load();
            Assert.Equal(HardwareBinding.Digest(Uuid, Bios), record.Digest);
            Assert.Equal("HW-NEW", record.HwId);
        }

        // Dondurma yazılımı identity.key'i imajdaki kimliğe döndürdü: anahtarın verildiği kimlik geri yazılır
        [Fact]
        public void Match_RestoresTheIdentityTheKeyWasIssuedTo()
        {
            Assert.True(Binding(Uuid, Bios).Bind("HW-CLONE", "test"));
            File.WriteAllText(AgentUpdate.IdentityPath, "HW-ORIGINAL");
            using Worker worker = NewWorker("HW-ORIGINAL");
            Assert.Equal(BindingVerdict.Match, worker.Binding.CheckOnStartup());
            worker.ApplyHardwareBinding(BindingVerdict.Match);
            Assert.Equal("HW-CLONE", worker.HwId);
            Assert.Equal("HW-CLONE", File.ReadAllText(AgentUpdate.IdentityPath));
        }

        [Fact]
        public void Clone_IsReportedWithTheNewIdentity()
        {
            Enrolled();
            using Worker worker = NewWorker("HW-DERIVED");
            worker.Binding = Binding(OtherUuid, OtherBios);
            Assert.Equal(BindingVerdict.Clone, worker.Binding.CheckOnStartup());
            worker.ApplyHardwareBinding(BindingVerdict.Clone);   // Olay Günlüğü yazılamasa da durmaz
            Assert.Equal("HW-DERIVED", worker.HwId);
            Assert.False(File.Exists(HardwareBinding.PrimaryPath));
        }

        [Fact]
        public void AuditEvents_ForCloneAndCloneRejection()
        {
            LocalAuditEvent detected = LocalAudit.CloneDetected("HW-ORIGINAL", "HW-DERIVED", @"C:\POpsData\secure\clone-20261002-093000",
                new[] { "identity.key", "agent.secret" }, true);
            Assert.Equal(1070, detected.EventId);
            Assert.Equal(LocalAuditLevel.Warning, detected.Level);
            Assert.Contains("old_hw_id: HW-ORIGINAL", detected.Message);
            Assert.Contains("new_hw_id: HW-DERIVED", detected.Message);
            Assert.Contains("files: identity.key, agent.secret", detected.Message);
            Assert.Contains("enroll_token: var", detected.Message);

            LocalAuditEvent rejected = LocalAudit.CloneRejected("command");
            Assert.Equal(1071, rejected.EventId);
            Assert.Equal(LocalAuditLevel.Warning, rejected.Level);
        }

        private sealed class FixedRandom : Random
        {
            private readonly double _value;
            public FixedRandom(double value) { _value = value; }
            public override double NextDouble() => _value;
        }

        [Fact]
        public void CloseCode4409_WaitsTenMinutesPlusJitter()
        {
            Assert.Equal(ReconnectBackoff.Rejection.Clone, ReconnectBackoff.FromCloseStatus(4409));
            Assert.Equal(ReconnectBackoff.Rejection.Auth, ReconnectBackoff.FromCloseStatus(4401));
            Assert.Equal(ReconnectBackoff.Rejection.None, ReconnectBackoff.FromCloseStatus(1000));
            Assert.Equal(ReconnectBackoff.Rejection.None, ReconnectBackoff.FromCloseStatus(null));

            for (int attempt = 0; attempt <= ReconnectBackoff.MaxAttempt; attempt++)
            {
                Assert.Equal(TimeSpan.FromMinutes(10), ReconnectBackoff.Delay(attempt, ReconnectBackoff.Rejection.Clone, new FixedRandom(0)));
                TimeSpan top = ReconnectBackoff.Delay(attempt, ReconnectBackoff.Rejection.Clone, new FixedRandom(0.9999));
                Assert.InRange(top, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11));
            }
            Assert.Equal(TimeSpan.FromMinutes(10) + ReconnectBackoff.Ceiling(5) * 0.5,
                ReconnectBackoff.Delay(5, ReconnectBackoff.Rejection.Clone, new FixedRandom(0.5)));
            // Eski imza değişmedi
            Assert.Equal(ReconnectBackoff.AuthRejectedMinimum, ReconnectBackoff.Delay(0, true, new FixedRandom(0)));
        }

        [Fact]
        public void CloneRejection_IsLoggedWithoutThrowing()
        {
            using Worker worker = NewWorker("HW-A");
            worker.OnCloneRejected(TimeSpan.FromMinutes(10.5));
            worker.OnCloneRejected(TimeSpan.FromMinutes(10.2));
        }
    }

    // POpsAgent.exe --generalize [--enroll-token <jeton>]
    public class GeneralizeTests : TestBase, IDisposable
    {
        private const string Token = "multi-use-token-0123456789abcdef";
        private readonly Func<bool> _isAdmin = Generalizer.IsAdministrator;
        private readonly Func<string> _stop = Generalizer.StopAgent;
        private int _stops;

        public GeneralizeTests()
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
            HardwareBinding.PrimaryPath,
            SecureStore.PathOf(ResultSpool.FileName),
            AgentUpdate.ResultPath,
            AgentUpdate.ReportedResultPath,
            SoftwareReporter.StatePath,
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
            int code = Generalizer.Run(args, output);
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

    // Yazılım envanteri: açılışta rastgele gecikme, değişmeyen listeyi 7 gün göndermeme
    public class InventoryLoadTests : TestBase, IDisposable
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
        private int _posts, _uploaded;
        private DateTime _now = T0;
        private string _hwId = "HW-I";
        private List<SoftwareItem> _items = Items("1");

        public InventoryLoadTests()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("inventory-data");
            SecureStore.Dir = TestEnvironment.NewDir("inventory-secure");
            AgentCredentials.SaveSecret("inventory-secret-0123456789abcdefghij", "HW-I");
        }

        public void Dispose()
        {
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
        }

        private static List<SoftwareItem> Items(string version) => new List<SoftwareItem>
        {
            new SoftwareItem { Name = "7-Zip", Version = "24.08" },
            new SoftwareItem { Name = "POps Test", Version = version },
        };

        // Her çağrı yeni bir raporlayıcı: servis yeniden başlamış gibi
        private SoftwareReporter Reporter() => new SoftwareReporter("https://pops.example", () => _hwId, () => _uploaded++)
        {
            Collector = () => _items,
            UtcNow = () => _now,
            Poster = (_, _) => { _posts++; return Task.FromResult(PostResult.Sent); },
        };

        [Fact]
        public void FirstReport_IsSpreadOverThirtyMinutesAfterTheFirstMinute()
        {
            Assert.Equal(TimeSpan.FromMinutes(1), SoftwareReporter.FirstDelay(0));
            Assert.Equal(TimeSpan.FromMinutes(16), SoftwareReporter.FirstDelay(0.5));
            Assert.InRange(SoftwareReporter.FirstDelay(0.99999), TimeSpan.FromMinutes(30.99), TimeSpan.FromMinutes(31));
            Assert.Equal(TimeSpan.FromMinutes(31), SoftwareReporter.FirstDelay(5));
            Assert.Equal(TimeSpan.FromMinutes(1), SoftwareReporter.FirstDelay(-1));
        }

        [Fact]
        public async Task UnchangedList_IsNotResentAfterRestart_ForSevenDays()
        {
            Assert.Null(await Reporter().ReportOnceAsync());
            Assert.Equal((1, 1), (_posts, _uploaded));
            Assert.True(File.Exists(SoftwareReporter.StatePath));

            _now = T0.AddDays(6.9);
            Assert.Null(await Reporter().ReportOnceAsync());
            // Gönderilmedi, ama last_inventory_upload yenilendi (panelde eski görünmez)
            Assert.Equal((1, 2), (_posts, _uploaded));

            _now = T0.AddDays(7);
            Assert.Null(await Reporter().ReportOnceAsync());
            Assert.Equal((2, 3), (_posts, _uploaded));
        }

        [Fact]
        public async Task ChangedListOrIdentity_IsSentAtOnce()
        {
            await Reporter().ReportOnceAsync();
            _items = Items("2");
            await Reporter().ReportOnceAsync();
            Assert.Equal(2, _posts);
            _hwId = "HW-J";
            await Reporter().ReportOnceAsync();
            Assert.Equal(3, _posts);
        }

        [Fact]
        public async Task FailedSend_IsNotRemembered()
        {
            var reporter = Reporter();
            reporter.Poster = (_, _) => Task.FromResult(PostResult.Failed);
            Assert.Equal(SoftwareReporter.RetryDelay, await reporter.ReportOnceAsync());
            Assert.False(File.Exists(SoftwareReporter.StatePath));
            Assert.Equal(0, _uploaded);
            await Reporter().ReportOnceAsync();
            Assert.Equal(1, _posts);
        }

        [Fact]
        public async Task ForgetLastReport_SendsAgain()
        {
            var reporter = Reporter();
            await reporter.ReportOnceAsync();
            reporter.ForgetLastReport();
            Assert.False(File.Exists(SoftwareReporter.StatePath));
            await reporter.ReportOnceAsync();
            Assert.Equal(2, _posts);
        }

        [Fact]
        public async Task CorruptState_IsIgnored()
        {
            File.WriteAllText(SoftwareReporter.StatePath, "{bozuk");
            await Reporter().ReportOnceAsync();
            Assert.Equal(1, _posts);
        }

        [Fact]
        public void ClockMovedBack_Sends()
        {
            var gate = new ReportGate(TimeSpan.FromDays(7));
            gate.MarkSent("h", T0, "HW-I");
            Assert.False(gate.ShouldSend("h", T0.AddDays(1), "HW-I"));
            Assert.True(gate.ShouldSend("h", T0.AddMinutes(-1), "HW-I"));
        }
    }
}
