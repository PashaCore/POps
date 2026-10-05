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
        // Testlerin kullandığı veri ve güvenli klasörler (hep geçici klasörün içinde)
        public static readonly string DefaultDataDir = Path.Combine(Root, "data-default");
        public static readonly string DefaultSecureDir = Path.Combine(Root, "secure-default");
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
            POps.Shared.POpsHelpers.ConfigPaths = new string[0];
            POpsAgent.SecureStore.SystemSid = me;
            POpsAgent.SecureStore.Dir = DefaultSecureDir;
            POpsAgent.AgentUpdate.DataDir = DefaultDataDir;
            // Gerçek kurulumda bu klasörler hep vardır; testler hangi sırayla çalışırsa çalışsın var olsunlar
            // (ör. karantina kaydını yazan test, klasörü oluşturan başka bir testten önce çalışabilir)
            Directory.CreateDirectory(DefaultSecureDir);
            Directory.CreateDirectory(DefaultDataDir);
            POps.Shared.ServerTrust.CaPath = Path.Combine(DefaultSecureDir, POps.Shared.ServerTrust.FileName);
            // Bütün test seam'leri süreç başında bir kez kurulur (karantina testleri gerçek Görev Yöneticisi / oturum
            // politikalarına, güvenlik duvarına, güç API'sine dokunmaz ...). Paralel sınıflar EnsureIsolated çağırmaz ve
            // statikleri değiştirmez: seri koleksiyonlar paralel sınıflardan sonra çalıştığı için bu değerleri görürler.
            EnsureIsolated();
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

#if !NETFRAMEWORK
        // Bir test gerçek klasörlere (C:\POpsData, ...) dönen bir yol bırakmışsa bir sonraki test öncesinde geçici
        // klasörlere geri alınır. (Türetilmiş sınıfın alan başlatıcıları temel kurucudan ÖNCE çalışır: orada okunan
        // yol, ortam kurulmadan önceki gerçek yol olabilir.)
        public static void EnsureIsolated()
        {
            if (!IsUnderRoot(POpsAgent.AgentUpdate.DataDir)) POpsAgent.AgentUpdate.DataDir = DefaultDataDir;
            if (!IsUnderRoot(POpsAgent.SecureStore.Dir)) POpsAgent.SecureStore.Dir = DefaultSecureDir;
            if (!IsUnderRoot(POps.Shared.ServerTrust.CaPath)) POps.Shared.ServerTrust.CaPath = Path.Combine(DefaultSecureDir, POps.Shared.ServerTrust.FileName);
            if (!IsUnderRoot(POps.Shared.POpsHelpers.MachineLogDir)) POps.Shared.POpsHelpers.MachineLogDir = DefaultMachineLogDir;
            POpsAgent.AgentDirectories.Problem = null;
            if (!(POpsAgent.KioskMode.Registry is FakeKioskRegistry)) POpsAgent.KioskMode.Registry = new FakeKioskRegistry();
            // Güncelleme indirmeleri testlerde BITS'e gitmez (BITS testleri sahte çalıştırıcıyla açar)
            POpsAgent.BitsDownload.Enabled = false;
            IsolatePeerCache();
            // Sunucu modülleri bellekte tutulur: her test hepsi açık başlar
            POpsAgent.AgentModules.Reset();
            // Güvenlik duvarı betikleri ve sınav izin listesinin ad çözümü gerçek sisteme gitmez (ör. paylaşılan protokol
            // vektörlerindeki exam_mode); bunları sınayan sınıflar kendi sahtelerini kurar
            POpsAgent.NetworkIsolation.ScriptRunner = _ => System.Threading.Tasks.Task.FromResult((1, "testlerde güvenlik duvarına dokunulmaz"));
            POpsAgent.ExamMode.Resolver = _ => System.Threading.Tasks.Task.FromResult(Array.Empty<System.Net.IPAddress>());
            // Sınav modunun uygulama engeli bu bilgisayardaki gerçek süreçleri (cmd.exe, powershell.exe) kapatmaz
            POpsAgent.ExamMode.StopProcess = _ => { };
            // Testler gerçek winget'i asla çalıştırmaz (ör. paylaşılan protokol vektörlerindeki winget_install); gereken sınıf
            // kendi sahtesini kurar
            POpsAgent.WingetInstall.Locator = () => null;
            IsolatePowerAndSessions();
        }

        // Testler bu makineyi asla kapatmaz, yeniden başlatmaz, oturumu kapatmaz ya da kilitlemez: gerçek güç API'sinin
        // yerine her çağrıda hata veren sahte, oturum sorgularının yerine "kullanıcı yok" konur. Test kendi sahtesini
        // koyar; bir sonraki test yeniden bu reddeden sahtelerle başlar.
        public static readonly Func<POpsAgent.PowerOperation, int, bool> RefusingPowerApi =
            (operation, taskId) => throw new InvalidOperationException($"Testte gerçek güç API'si çağrıldı ({operation}, görev {taskId}).");

        public static void IsolatePowerAndSessions()
        {
            POpsAgent.PowerActions.Execute = RefusingPowerApi;
            POpsAgent.PowerActions.TrayLockTimeout = TimeSpan.FromSeconds(5);
            POpsAgent.SessionTasks.HasConsoleUser = () => false;
            POpsAgent.SessionTasks.ConsoleSession = () => POps.Shared.UserSessionLauncher.NoSession;
            POpsAgent.SessionTasks.Delay = System.Threading.Tasks.Task.Delay;
            POpsAgent.UserMessages.AckTimeout = TimeSpan.FromMinutes(30);
        }

        // Eş önbelleği: önceki testin sunucusu kapanır; güvenlik duvarına asla dokunulmaz (betik reddedilir), sunucu
        // yalnızca 127.0.0.1'de rastgele bir portta açılır (8817'de, bütün arabirimlerde değil); saat ve denetimler gerçek
        private static void IsolatePeerCache()
        {
            POpsAgent.PeerCache.ResetState();
            POpsAgent.PeerCache.FirewallRunner = _ => System.Threading.Tasks.Task.FromResult((1, "testlerde güvenlik duvarına dokunulmaz"));
            POpsAgent.PeerCache.ListenEndpoint = () => new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0);
            POpsAgent.PeerCache.Clock = () => DateTime.UtcNow;
            POpsAgent.PeerCache.IsLocalSubnet = POpsAgent.PeerCache.InLocalSubnet;
            POpsAgent.PeerCache.IsIsolated = POpsAgent.PeerCache.DefaultIsolated;
            POpsAgent.PeerDownload.AllowLoopbackPeers = false;
            POpsAgent.PeerDownload.HeaderTimeout = POpsAgent.PeerDownload.DefaultHeaderTimeout;
            POpsAgent.PeerDownload.StallTimeout = POpsAgent.PeerDownload.DefaultStallTimeout;
        }

        private static bool IsUnderRoot(string path) =>
            !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(Root, StringComparison.OrdinalIgnoreCase);
#endif
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

    // SharedState koleksiyonunun tabanı: her testten önce yolları ve test seam'lerini test değerlerine geri alır. Bundan
    // türeyen sınıf [Collection(SharedStateCollection.Name)] taşır; paralel bir sınıf bundan türemez (ParallelSafetyTests)
    public abstract class SharedStateTestBase : TestBase
    {
        protected SharedStateTestBase()
        {
#if !NETFRAMEWORK
            TestEnvironment.EnsureIsolated();
#endif
        }
    }
}
