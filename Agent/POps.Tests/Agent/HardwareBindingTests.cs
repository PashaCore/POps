using System;
using System.Collections.Generic;
using System.IO;
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

        private static BindingRecord BothReal => HardwareBinding.CreateRecord(Uuid, Bios, "HW-A", T0);

        // Kopya imaj başka makinede ikisini birden değiştirir
        [Fact]
        public void Evaluate_BothComparableValuesChanged_IsClone()
        {
            var result = HardwareBinding.Compare(BothReal, OtherUuid, OtherBios);
            Assert.Equal(BindingVerdict.Clone, result.Verdict);
            Assert.Equal(new[] { "uuid", "bios_sn" }, result.Changed);
            Assert.Empty(result.Same);
        }

        // Anakart servisi, BIOS seri numarası düzeltmesi, sanal makine ayarı: karar verilmez
        [Theory]
        [InlineData(OtherUuid, Bios, "uuid", "bios_sn")]
        [InlineData(Uuid, OtherBios, "bios_sn", "uuid")]
        public void Evaluate_OneSameOneChanged_IsInconclusive(string uuid, string bios, string changed, string same)
        {
            var result = HardwareBinding.Compare(BothReal, uuid, bios);
            Assert.Equal(BindingVerdict.Inconclusive, result.Verdict);
            Assert.Equal(new[] { changed }, result.Changed);
            Assert.Equal(new[] { same }, result.Same);
        }

        // Yalnız biri karşılaştırılabiliyor (bağlarken ya da şimdi diğeri güvenilir okunamadı) ve o farklı: kopya
        [Theory]
        [InlineData(OtherUuid, "NULL")]
        [InlineData("NULL", OtherBios)]
        [InlineData(OtherUuid, "Default string")]
        public void Evaluate_OnlyComparableValueChanged_IsClone(string uuid, string bios) =>
            Assert.Equal(BindingVerdict.Clone, HardwareBinding.Evaluate(BothReal, uuid, bios));

        // Yalnız biri karşılaştırılabiliyor ve o aynı: aynı bilgisayar (Match). Özetteki fark yalnızca güvenilir
        // okunamayan parçadan gelir; o parça ne kopyayı ne de aynı makineyi kanıtlar, güvenilir kanıt aynı makineyi
        // gösterir. Inconclusive her açılışta Olay Günlüğüne uyarı yazardı (ör. BIOS seri numarası bir an okunamadı).
        [Theory]
        [InlineData(Uuid, "NULL")]
        [InlineData("NULL", Bios)]
        [InlineData(Uuid, "To be filled by O.E.M.")]
        public void Evaluate_OnlyComparableValueSame_IsMatch(string uuid, string bios) =>
            Assert.Equal(BindingVerdict.Match, HardwareBinding.Evaluate(BothReal, uuid, bios));

        [Fact]
        public void Evaluate_UnreliableBiosAtBindTime_UuidDecidesAlone()
        {
            var record = HardwareBinding.CreateRecord(Uuid, "Default string", "HW-A", T0);
            Assert.Null(record.BiosSerial);
            Assert.Equal(BindingVerdict.Clone, HardwareBinding.Evaluate(record, OtherUuid, "Default string"));
            // Yalnızca güvenilmez parça değişti (ör. BIOS güncellemesi seri numarasını doldurdu): aynı bilgisayar
            Assert.Equal(BindingVerdict.Match, HardwareBinding.Evaluate(record, Uuid, Bios));
        }

        [Theory]
        [InlineData("NULL", "NULL")]
        [InlineData("00000000-0000-0000-0000-000000000000", "Default string")]
        [InlineData("03000200-0400-0500-0006-000700080009", "0000000")]
        public void Evaluate_NothingComparable_IsUnreadable(string uuid, string bios) =>
            Assert.Equal(BindingVerdict.Unreadable, HardwareBinding.Evaluate(BothReal, uuid, bios));

        private static string PendingResults => SecureStore.PathOf(ResultSpool.FileName);

        private static void PendingResult(int taskId) =>
            new ResultSpool(PendingResults).Add(taskId, new { type = "result", task_id = taskId, exit_code = 0 });

        [Fact]
        public void Startup_Clone_MovesDeviceFilesAsideWithoutDeleting()
        {
            Enrolled();
            PendingResult(7);
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCredentials.EnrollTokenFileName), "enroll-token-0123456789abcdef");

            var binding = Binding(OtherUuid, OtherBios);
            Assert.Equal(BindingVerdict.Clone, binding.CheckOnStartup());

            string folder = SecureStore.PathOf("clone-20261002-093000");
            Assert.Equal(folder, binding.CloneFolder);
            Assert.Equal("HW-ORIGINAL", binding.PreviousHwId);
            Assert.Equal(new[] { "identity.key", "agent.secret", "bypass.device", "hw.bind", "pending-results.json" }, binding.MovedFiles);
            foreach (string name in binding.MovedFiles) Assert.True(File.Exists(Path.Combine(folder, name)), name);
            Assert.False(File.Exists(AgentUpdate.IdentityPath));
            Assert.False(File.Exists(SecureStore.PathOf(AgentCredentials.SecretFileName)));
            Assert.False(File.Exists(SecureStore.PathOf(AgentCredentials.DeviceBypassSecretFileName)));
            Assert.False(File.Exists(HardwareBinding.PrimaryPath));
            Assert.False(File.Exists(PendingResults));
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

        private static void AssertUntouched(HardwareBinding binding)
        {
            Assert.Null(binding.CloneFolder);
            Assert.Empty(binding.MovedFiles);
            Assert.True(File.Exists(AgentUpdate.IdentityPath));
            Assert.True(File.Exists(SecureStore.PathOf(AgentCredentials.SecretFileName)));
            Assert.True(File.Exists(SecureStore.PathOf(AgentCredentials.DeviceBypassSecretFileName)));
            Assert.True(File.Exists(HardwareBinding.PrimaryPath));
            Assert.True(File.Exists(PendingResults));
            Assert.Empty(Directory.GetDirectories(SecureStore.Dir, "clone-*"));
        }

        [Fact]
        public void Startup_Unreadable_TouchesNothing()
        {
            Enrolled();
            PendingResult(7);
            var binding = Binding("-", "To be filled by O.E.M.");
            Assert.Equal(BindingVerdict.Unreadable, binding.CheckOnStartup());
            AssertUntouched(binding);
        }

        [Fact]
        public void Startup_PartlyChanged_TouchesNothing()
        {
            Enrolled();
            PendingResult(7);
            var binding = Binding(Uuid, OtherBios);
            Assert.Equal(BindingVerdict.Inconclusive, binding.CheckOnStartup());
            Assert.Equal(new[] { "bios_sn" }, binding.ChangedParts);
            Assert.Equal(new[] { "uuid" }, binding.SameParts);
            AssertUntouched(binding);
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

        // Asıl cihazın onay bekleyen sonuçları servis açılırken okunmuştu: klonda bellekten de bırakılır, yeni kimlikle gitmez
        [Fact]
        public async Task Clone_DropsTheOriginalsPendingResults()
        {
            Enrolled();
            PendingResult(7);
            PendingResult(8);
            var sent = new List<JsonElement>();
            using Worker worker = new Worker(NullLogger<Worker>.Instance)
            {
                HwId = "HW-DERIVED",
                SendOverride = payload => { sent.Add(JsonSerializer.SerializeToElement(payload)); return Task.FromResult(true); },
                Binding = Binding(OtherUuid, OtherBios),
            };
            Assert.Equal(2, worker.Results.Count);

            Assert.Equal(BindingVerdict.Clone, worker.Binding.CheckOnStartup());
            worker.ApplyHardwareBinding(BindingVerdict.Clone);
            Assert.Equal(0, worker.Results.Count);
            Assert.False(File.Exists(PendingResults));
            Assert.True(File.Exists(Path.Combine(worker.Binding.CloneFolder, ResultSpool.FileName)));

            // Onaylı sunucu: diskteki ve bellekteki onaysız sonuçlar her heartbeat'te gönderilirdi
            worker.Handshake.OnServerInfo(JsonDocument.Parse("{\"action\":\"server_info\",\"features\":[\"result_ack\"]}").RootElement);
            await worker.FlushPendingResultsAsync();
            Assert.DoesNotContain(sent, m => m.TryGetProperty("type", out var t) && t.GetString() == "result");
        }

        [Fact]
        public void PartlyChanged_KeepsTheKeyAndPendingResults()
        {
            Enrolled();
            PendingResult(7);
            using Worker worker = NewWorker("HW-ORIGINAL");
            worker.Binding = Binding(OtherUuid, Bios);
            Assert.Equal(BindingVerdict.Inconclusive, worker.Binding.CheckOnStartup());
            worker.ApplyHardwareBinding(BindingVerdict.Inconclusive);   // Olay Günlüğüne 1072
            Assert.Equal(1, worker.Results.Count);
            Assert.Equal("HW-ORIGINAL", worker.HwId);
            AssertUntouched(worker.Binding);
        }

        [Fact]
        public void CloneRejection_IsLoggedWithoutThrowing()
        {
            using Worker worker = NewWorker("HW-A");
            worker.OnCloneRejected(TimeSpan.FromMinutes(10.5));
            worker.OnCloneRejected(TimeSpan.FromMinutes(10.2));
        }
    }
}
