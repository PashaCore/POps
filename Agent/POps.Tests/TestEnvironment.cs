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
            POpsAgent.SecureStore.Dir = Path.Combine(Root, "secure-default");
            POpsAgent.AgentUpdate.DataDir = Path.Combine(Root, "data-default");
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
    }

    // Statik kurucunun her test sınıfından önce çalışmasını garanti eder
    public abstract class TestBase
    {
        protected TestBase() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TestEnvironment).TypeHandle);
    }
}
