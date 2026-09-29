using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using POps.Installer;
using POps.Shared;
using Xunit;

namespace POps.Tests.Installer
{
    // MSI kaldırılırken karantina kilit politikaları kayıttaki önceki değerlere döner, kayıt silinir
    public class KioskRestoreSetupTests : TestBase
    {
        private const string User = @"HKU\S-1-5-21-1-2-3-1001";

        [Fact]
        public void Uninstall_RestoresAndDeletesTheRecord()
        {
            string secure = TestEnvironment.NewDir("kiosk-secure");
            var layout = new Layout { DataDir = Path.GetDirectoryName(secure), SecureDir = secure, LegacyDirs = new string[0] };
            var registry = new FakeKioskRegistry();
            registry.Hives.Add(User);
            string taskMgr = FakeKioskRegistry.Path(User, KioskPolicies.SystemKey, "DisableTaskMgr");
            registry.Values[taskMgr] = 0;

            // Ajanın yazdığı kayıt (aynı alan adları)
            List<KioskEntry> record = KioskPolicies.Plan(registry, null);
            KioskPolicies.Enforce(registry, record);
            File.WriteAllText(Path.Combine(secure, Setup.KioskRecordFile), new JavaScriptSerializer().Serialize(record));
            Assert.Equal(1, registry.Values[taskMgr]);

            var log = new List<string>();
            Setup.RestoreKiosk(layout, registry, log.Add);
            Assert.Equal(0, registry.Values[taskMgr]);
            Assert.Single(registry.Values);
            Assert.False(File.Exists(Path.Combine(secure, Setup.KioskRecordFile)));
            Assert.Contains(log, l => l.Contains("geri alındı"));

            // Kayıt yokken hiçbir şey yapılmaz
            Setup.RestoreKiosk(layout, registry, log.Add);
            Assert.Equal(0, registry.Values[taskMgr]);
        }
    }
}
