using System;
using System.IO;
using System.Linq;
using System.Security.Principal;

namespace POps.Tests
{
    // Bütün testler için ortak ortam: gerçek C:\POpsData, C:\POpsLogs ve C:\POps'a asla dokunulmaz; her şey geçici
    // bir klasörde olur. "Servis hesabı" testi çalıştıran kullanıcıdır (korumalı dosyaları o yazıp değiştirebilsin),
    // böylece testler hem yönetici olmayan bir geliştiricide hem de CI'da aynı çalışır.
    internal static class TestEnvironment
    {
        public static readonly string Root = Path.Combine(Path.GetTempPath(), "pops-tests", Guid.NewGuid().ToString("N").Substring(0, 8));

#if !NETFRAMEWORK
        public static readonly string[] DefaultConfigPaths;
        // SYSTEM bileşenlerinin log klasörü (loglar yine LogDirectoryOverride'a gider; bu yalnızca yol olarak kullanılır)
        public static readonly string DefaultMachineLogDir = Path.Combine(Root, "machine-logs");
#endif

        static TestEnvironment()
        {
            Directory.CreateDirectory(Root);
            // Korumalı dosyaların ACL'inde test kullanıcısı "servis hesabı" olarak yer aldığı için silinebilirler
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Root, true); } catch { } };
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
            // Klasör izinleri (veri, log, secure) testte "servis hesabı" olarak test kullanıcısına verilir
            POps.Shared.FolderSettings.SystemSid = me;
#if NETFRAMEWORK
            POps.Installer.Setup.SystemSid = me;
#else
            DefaultConfigPaths = POps.Shared.POpsHelpers.ConfigPaths.ToArray();
            POps.Shared.POpsHelpers.LogDirectoryOverride = Path.Combine(Root, "logs");
            POps.Shared.POpsHelpers.MachineLogDir = DefaultMachineLogDir;
            // Bağlamı olmayan ayar okumaları (POpsHelpers'ın parametresiz biçimleri) gerçek C:\POps\appsettings.json'u okumaz
            POps.Shared.POpsHelpers.ConfigPaths = new string[0];
            POpsAgent.SecureStore.SystemSid = me;
            // Ajanın yolları, çalışma durumu ve makine kapıları her testin kendi bağlamındadır (bkz. AgentHarness): makineye
            // dokunan her kapı orada reddeden sahtedir. Burada sıfırlanacak test seam'i kalmadı (Worker bölmesinin b3 adımı).
#endif
        }

        // Testin kendi boş klasörü
        public static string NewDir(string name)
        {
            string dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

#if !NETFRAMEWORK
        // Uzaktan komut çalıştırıcısı: görev dosyaları (pops_task_*.bat) gerçek %TEMP%'e değil, testin kendi klasörüne yazılır
        public static POpsAgent.CommandRunner NewCommandRunner(TimeSpan? maxDuration = null) =>
            new POpsAgent.CommandRunner(maxDuration: maxDuration, taskDirectory: NewDir("tasks"));
#endif

        // Depo kökü (VERSION dosyasını arayarak)
        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("VERSION dosyası bulunamadı");
        }

        public static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    }

    // Statik kurucunun her test sınıfından önce çalışmasını garanti eder. Paralel çalışan sınıfların tabanıdır: hiçbir
    // statiği değiştirmez.
    public abstract class TestBase
    {
        protected TestBase()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestEnvironment).TypeHandle);
        }
    }

    // SharedState koleksiyonunun tabanı: sınıfın süreç geneli bir değere (ortam değişkeni, ayar dosyaları, servis hesabı,
    // kendi statik önbelleği, MSI custom action'larının güvenilir kökü) dokunduğunu gösterir. Worker bölmesinin b3 adımından
    // beri her testten önce sıfırlanacak test seam'i yok. Bundan türeyen sınıf [Collection(SharedStateCollection.Name)]
    // taşır; paralel bir sınıf bundan türemez (ParallelSafetyTests)
    public abstract class SharedStateTestBase : TestBase
    {
    }
}
