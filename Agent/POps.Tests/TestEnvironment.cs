using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Xunit;

// Testler statik durumu (SecureStore.Dir, POpsHelpers.ConfigPaths ...) değiştirdiği için sırayla çalışır
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
        // Testlerin kullandığı veri ve güvenli klasörler (hep geçici klasörün içinde)
        public static readonly string DefaultDataDir = Path.Combine(Root, "data-default");
        public static readonly string DefaultSecureDir = Path.Combine(Root, "secure-default");
#endif

        static TestEnvironment()
        {
            Directory.CreateDirectory(Root);
            // Korumalı dosyaların ACL'inde test kullanıcısı "servis hesabı" olarak yer aldığı için silinebilirler
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Root, true); } catch { } };
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
#if NETFRAMEWORK
            POps.Installer.Setup.SystemSid = me;
#else
            DefaultConfigPaths = POps.Shared.POpsHelpers.ConfigPaths.ToArray();
            POps.Shared.POpsHelpers.LogDirectoryOverride = Path.Combine(Root, "logs");
            POps.Shared.POpsHelpers.ConfigPaths = new string[0];
            POpsAgent.SecureStore.SystemSid = me;
            POpsAgent.SecureStore.Dir = DefaultSecureDir;
            POpsAgent.AgentUpdate.DataDir = DefaultDataDir;
            // Gerçek kurulumda bu klasörler hep vardır; testler hangi sırayla çalışırsa çalışsın var olsunlar
            // (ör. karantina kaydını yazan test, klasörü oluşturan başka bir testten önce çalışabilir)
            Directory.CreateDirectory(DefaultSecureDir);
            Directory.CreateDirectory(DefaultDataDir);
            POps.Shared.ServerTrust.CaPath = Path.Combine(DefaultSecureDir, POps.Shared.ServerTrust.FileName);
            // Karantina testleri gerçek Görev Yöneticisi / oturum politikalarına dokunmasın
            POpsAgent.KioskMode.Registry = new FakeKioskRegistry();
#endif
        }

        // Testin kendi boş klasörü
        public static string NewDir(string name)
        {
            string dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // Depo kökü (VERSION dosyasını arayarak)
        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("VERSION dosyası bulunamadı");
        }

        public static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

#if !NETFRAMEWORK
        // Bir test gerçek klasörlere (C:\POpsData, ...) dönen bir yol bırakmışsa bir sonraki test öncesinde geçici
        // klasörlere geri alınır. (Türetilmiş sınıfın alan başlatıcıları temel kurucudan ÖNCE çalışır: orada okunan
        // yol, ortam kurulmadan önceki gerçek yol olabilir.)
        public static void EnsureIsolated()
        {
            if (!IsUnderRoot(POpsAgent.AgentUpdate.DataDir)) POpsAgent.AgentUpdate.DataDir = DefaultDataDir;
            if (!IsUnderRoot(POpsAgent.SecureStore.Dir)) POpsAgent.SecureStore.Dir = DefaultSecureDir;
            if (!IsUnderRoot(POps.Shared.ServerTrust.CaPath)) POps.Shared.ServerTrust.CaPath = Path.Combine(DefaultSecureDir, POps.Shared.ServerTrust.FileName);
            if (!(POpsAgent.KioskMode.Registry is FakeKioskRegistry)) POpsAgent.KioskMode.Registry = new FakeKioskRegistry();
            // Güncelleme indirmeleri testlerde BITS'e gitmez (BITS testleri sahte çalıştırıcıyla açar)
            POpsAgent.BitsDownload.Enabled = false;
            // Sunucu modülleri bellekte tutulur: her test hepsi açık başlar
            POpsAgent.AgentModules.Reset();
            // Güvenlik duvarı betikleri ve sınav izin listesinin ad çözümü gerçek sisteme gitmez (ör. paylaşılan protokol
            // vektörlerindeki exam_mode); bunları sınayan sınıflar kendi sahtelerini kurar
            POpsAgent.NetworkIsolation.ScriptRunner = _ => System.Threading.Tasks.Task.FromResult((1, "testlerde güvenlik duvarına dokunulmaz"));
            POpsAgent.ExamMode.Resolver = _ => System.Threading.Tasks.Task.FromResult(Array.Empty<System.Net.IPAddress>());
        }

        private static bool IsUnderRoot(string path) =>
            !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(Root, StringComparison.OrdinalIgnoreCase);
#endif
    }

    // Statik kurucunun her test sınıfından önce çalışmasını garanti eder
    public abstract class TestBase
    {
        protected TestBase()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestEnvironment).TypeHandle);
#if !NETFRAMEWORK
            TestEnvironment.EnsureIsolated();
#endif
        }
    }
}
