using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Karantinada Ctrl+Alt+Del seçenekleri: uygula / geri al (saf mantık) ve kayıt dosyasıyla açılış temizliği
    [Collection(SharedStateCollection.Name)]
    public class KioskPoliciesTests : SharedStateTestBase
    {
        private const string User = @"HKU\S-1-5-21-1-2-3-1001";
        private static readonly string TaskMgrUser = FakeKioskRegistry.Path(User, KioskPolicies.SystemKey, "DisableTaskMgr");
        private static readonly string TaskMgrMachine = FakeKioskRegistry.Path(KioskPolicies.Machine, KioskPolicies.SystemKey, "DisableTaskMgr");
        private static readonly string FastSwitch = FakeKioskRegistry.Path(KioskPolicies.Machine, KioskPolicies.SystemKey, "HideFastUserSwitching");
        private static readonly string NoLogoff = FakeKioskRegistry.Path(User, KioskPolicies.ExplorerKey, "NoLogoff");

        private static FakeKioskRegistry NewRegistry()
        {
            var r = new FakeKioskRegistry();
            r.Hives.Add(User);
            return r;
        }

        private static List<KioskEntry> Lock(IKioskRegistry r, List<KioskEntry> record = null)
        {
            List<KioskEntry> planned = KioskPolicies.Plan(r, record);
            Assert.Empty(KioskPolicies.Enforce(r, planned));
            return planned;
        }

        [Fact]
        public void Lock_SetsEveryOptionInMachineAndUserHives()
        {
            var r = NewRegistry();
            Lock(r);
            foreach (string name in new[] { "DisableTaskMgr", "DisableLockWorkstation", "DisableChangePassword" })
                Assert.Equal(1, r.Values[FakeKioskRegistry.Path(User, KioskPolicies.SystemKey, name)]);
            Assert.Equal(1, r.Values[NoLogoff]);
            // Makinede yalnızca sahada etkili ölçülen ikisi
            Assert.Equal(1, r.Values[FastSwitch]);
            Assert.Equal(1, r.Values[TaskMgrMachine]);
            Assert.Equal(6, r.Values.Count);
        }

        [Fact]
        public void NoPreviousValue_IsDeletedOnUnlock()
        {
            var r = NewRegistry();
            List<KioskEntry> record = Lock(r);
            Assert.Empty(KioskPolicies.Restore(r, record));
            Assert.Empty(r.Values);
        }

        [Fact]
        public void PreviousValue_IsPutBack()
        {
            var r = NewRegistry();
            r.Values[TaskMgrUser] = 0;
            r.Values[FastSwitch] = 2;
            List<KioskEntry> record = Lock(r);
            Assert.Equal(1, r.Values[TaskMgrUser]);
            KioskPolicies.Restore(r, record);
            Assert.Equal(0, r.Values[TaskMgrUser]);
            Assert.Equal(2, r.Values[FastSwitch]);
            Assert.False(r.Values.ContainsKey(NoLogoff));
        }

        // Kurumun kendi politikası zaten açıktı: yalnızca bizimki geri alınır, onunki açık kalır
        [Fact]
        public void PolicyAlreadyOn_StaysOn()
        {
            var r = NewRegistry();
            r.Values[TaskMgrMachine] = 1;
            List<KioskEntry> record = Lock(r);
            KioskPolicies.Restore(r, record);
            Assert.Equal(1, r.Values[TaskMgrMachine]);
            Assert.False(r.Values.ContainsKey(TaskMgrUser));
        }

        // Bizden sonra başkası değiştirdiyse dokunulmaz
        [Fact]
        public void ChangedByOthersDuringTheLock_IsLeftAlone()
        {
            var r = NewRegistry();
            List<KioskEntry> record = Lock(r);
            r.Values[TaskMgrUser] = 0;
            KioskPolicies.Restore(r, record);
            Assert.Equal(0, r.Values[TaskMgrUser]);
        }

        // Kilit yeniden uygulansa da (tepsi bağlandı, açılış) ilk önceki değer korunur
        [Fact]
        public void LockingAgain_KeepsTheFirstPreviousValue()
        {
            var r = NewRegistry();
            r.Values[TaskMgrUser] = 0;
            List<KioskEntry> record = Lock(r);
            record = Lock(r, record);
            Assert.Equal(record.Count, record.Select(e => e.Hive + e.Key + e.Name).Distinct().Count());
            KioskPolicies.Restore(r, record);
            Assert.Equal(0, r.Values[TaskMgrUser]);
        }

        // Karantinada oturumu kapatan kullanıcı: kovanı yokken bekletilir, dönünce geri alınır
        [Fact]
        public void UserWhoSignedOut_IsRestoredWhenBack()
        {
            var r = NewRegistry();
            List<KioskEntry> record = Lock(r);
            r.Hives.Clear();
            List<KioskEntry> pending = KioskPolicies.Restore(r, record);
            Assert.Equal(4, pending.Count);
            Assert.All(pending, e => Assert.Equal(User, e.Hive));
            Assert.Equal(1, r.Values[TaskMgrUser]);
            Assert.False(r.Values.ContainsKey(TaskMgrMachine));
            r.Hives.Add(User);
            Assert.Empty(KioskPolicies.Restore(r, pending));
            Assert.Empty(r.Values);
        }

        [Fact]
        public void WriteFailure_IsReportedAndKeptForLater()
        {
            var r = NewRegistry();
            r.FailWrites = hive => hive == KioskPolicies.Machine;
            List<KioskEntry> record = KioskPolicies.Plan(r, null);
            Assert.Equal(2, KioskPolicies.Enforce(r, record).Count);
            Assert.Equal(1, r.Values[TaskMgrUser]);
        }

        // ------------------------------------------------------------------ ajan: kayıt dosyası ve açılış temizliği

        [Fact]
        public void Agent_RecordIsWritten_AndStartupWithoutLockCleansUp()
        {
            var r = NewRegistry();
            r.Values[TaskMgrUser] = 0;
            KioskMode.Registry = r;
            try
            {
                KioskMode.Engage();
                Assert.True(File.Exists(KioskMode.RecordPath));
                var saved = JsonSerializer.Deserialize<List<KioskEntry>>(File.ReadAllText(KioskMode.RecordPath));
                Assert.Contains(saved, e => e.Hive == User && e.Name == "DisableTaskMgr" && e.HadValue && e.PreviousValue == 0);
                Assert.Equal(1, r.Values[TaskMgrUser]);

                // Karantina sürerken yeniden başlatma: ayarlar yerinde kalır
                KioskMode.Sync(true);
                Assert.Equal(1, r.Values[TaskMgrUser]);

                // Açılışta karantina yok: önceki değerler, kayıt silinir
                KioskMode.Sync(false);
                Assert.Equal(0, r.Values[TaskMgrUser]);
                Assert.False(r.Values.ContainsKey(FastSwitch));
                Assert.False(File.Exists(KioskMode.RecordPath));
            }
            finally { KioskMode.Registry = new FakeKioskRegistry(); }
        }

        [Fact]
        public void Agent_ReleaseWithoutRecord_DoesNothing()
        {
            var r = NewRegistry();
            r.Values[TaskMgrUser] = 1;
            KioskMode.Registry = r;
            try
            {
                KioskMode.Release();
                Assert.Equal(1, r.Values[TaskMgrUser]);
            }
            finally { KioskMode.Registry = new FakeKioskRegistry(); }
        }
    }
}
