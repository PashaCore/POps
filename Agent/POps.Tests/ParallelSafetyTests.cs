using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace POps.Tests
{
    // Paralel çalışmanın kuralı. Hiçbir test sınıfı değişebilir bir statiğe (yol ayarları, test seam'leri, bellekteki ajan
    // durumu, ortam değişkenleri) dokunmaz: her test kendi nesnelerini kurar (bkz. AgentHarness). Gerçek süreç başlatan,
    // adlandırılmış boru ya da dinleyen soket açan sınıflar seridir (Machine). Bir sınıfın nelere ulaştığı, kendi kodundan ve
    // çağırdığı ürün kodundan IL ile bulunur (StaticAccessScanner; sınırları orada yazılı). Süre ölçen testler ve tarayıcının
    // göremediği yollardan süreç başlatanlar IL'den anlaşılmaz: onlar elle Machine'e konur (MachineByHand).
    public class ParallelSafetyTests : TestBase
    {
        // Bütün sınıfların okuyabildiği statikler: test süreci başında TestEnvironment bir kez kurar, sonra hiçbir test değiştirmez
        // (yazmak her yerde yasak). Ürün kodunda da süreç geneli değerlerdir (bkz. ProductStaticTests).
        private static readonly Dictionary<string, string> ReadAnywhere = new Dictionary<string, string>
        {
            ["POpsHelpers.LogDirectoryOverride"] = "bütün testlerin tek log klasörü; POpsHelpers.Log kilit altında yazar",
            ["POpsHelpers.MachineLogDir"] = "POpsHelpers.Log okur; testlerde LogDirectoryOverride dolu olduğu için kullanılmaz",
            ["FolderSettings.SystemSid"] = "servis hesabı (testte test kullanıcısı); korumalı dosya ve klasör ACL'leri okur",
            ["SecureStore.SystemSid"] = "servis hesabı (testte test kullanıcısı); başka bir hesapla denetleyen test onu argüman olarak verir",
            ["Setup.SystemSid"] = "MSI custom action'larının servis hesabı (testte test kullanıcısı)",
        };

        // Tarayıcının göremediği bir nedenle Machine'de olan sınıflar ve nedenleri. Burada olmayan her Machine sınıfı bir makine
        // kaynağına IL'den ulaşır (MachineClasses_StillNeedTheMachine); ulaşmayan sınıf paralel çalışır.
        private static readonly Dictionary<string, string> MachineByHand = new Dictionary<string, string>
        {
#if !NETFRAMEWORK
            ["WindowsUpdateAgentTests"] = "geçen gerçek süreyi ölçer (HungJob_IsAbortedAndNotWaitedForever); WUA çağrıları dynamic",
            ["DnsDomainIndexTests"] = "geçen gerçek süreyi ölçer (LargeLists_CheckQuickly)",
            ["WorkerCommandTests"] = "uzaktan komutlar gerçek CommandRunner ile cmd.exe'de çalışır, sonuç süreyle beklenir; süreç komut "
                + "dağıtıcısının arayüz çağrısının arkasında (IL'den görünmez)",
            ["WorkerResultAckTests"] = "uzaktan komutlar gerçek CommandRunner ile cmd.exe'de çalışır; süreç komut dağıtıcısının arayüz "
                + "çağrısının arkasında (IL'den görünmez)",
            ["ModulesTests"] = "uzaktan komutlar gerçek CommandRunner ile cmd.exe'de çalışır; süreç komut dağıtıcısının arayüz çağrısının "
                + "arkasında (IL'den görünmez)",
#endif
        };

        private static List<Type> TestClasses() =>
            typeof(ParallelSafetyTests).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.GetMethods().Any(m => m.IsDefined(typeof(FactAttribute), true)))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();

        // Sınıfın üzerinde yazan koleksiyon adı (kalıtılan değil: hangi koleksiyonda olduğu sınıfın üstünde görünmeli)
        private static string CollectionOf(Type type) =>
            type.GetCustomAttributesData()
                .Where(a => a.AttributeType == typeof(CollectionAttribute))
                .Select(a => (string)a.ConstructorArguments[0].Value)
                .SingleOrDefault();

        private static IEnumerable<StaticAccessScanner.Finding> StateFindings(Type type) =>
            StaticAccessScanner.Scan(type).Where(f => f.Kind != StaticAccessScanner.Kind.Machine
                && !(f.Kind == StaticAccessScanner.Kind.Read && ReadAnywhere.ContainsKey(f.Member)));

        private static IEnumerable<StaticAccessScanner.Finding> MachineFindings(Type type) =>
            StaticAccessScanner.Scan(type).Where(f => f.Kind == StaticAccessScanner.Kind.Machine);

        private static string Report(IEnumerable<(Type Type, StaticAccessScanner.Finding Finding)> findings) =>
            string.Join(Environment.NewLine, findings.Select(x => $"  {x.Type.Name}: {x.Finding}"));

        [Fact]
        public void NoTestClass_TouchesMutableStatics()
        {
            var findings = TestClasses()
                .SelectMany(t => StateFindings(t).Select(f => (t, f)))
                .ToList();
            Assert.True(findings.Count == 0,
                "Bu sınıflar değişebilir bir statiğe dokunuyor. Statiği kaldırın: değeri testin kendi nesnesine (AgentHarness, " +
                "kurucu ya da yöntem argümanı) taşıyın; seri bir koleksiyon bunun yerine geçmez:" + Environment.NewLine + Report(findings));
        }

        [Fact]
        public void ParallelClasses_UseNoMachineResources()
        {
            var findings = TestClasses()
                .Where(t => CollectionOf(t) == null)
                .SelectMany(t => MachineFindings(t).Select(f => (t, f)))
                .ToList();
            Assert.True(findings.Count == 0,
                "Bu sınıflar paralel ama gerçek süreç, adlandırılmış boru, soket ya da Thread.Sleep kullanıyor. Sınıfı " +
                "[Collection(MachineCollection.Name)] ile işaretleyin ve nedenini yorumla yazın:" + Environment.NewLine + Report(findings));
        }

        // Machine'deki her sınıf bir makine kaynağına IL'den ulaşır ya da elle konma nedeni MachineByHand'dedir; makine
        // kaynağı kalmayan sınıf (ör. bir makine kapısı sahteye döndü) paralel çalışır, koleksiyon böylece yalnızca küçülür.
        // Yalnızca Machine adı kullanılır.
        [Fact]
        public void MachineClasses_StillNeedTheMachine()
        {
            var wrong = new List<string>();
            List<Type> classes = TestClasses();
            foreach (Type type in classes)
            {
                string collection = CollectionOf(type);
                if (collection == null) continue;
                if (collection != MachineCollection.Name) wrong.Add($"  {type.Name}: bilinmeyen koleksiyon \"{collection}\"");
                else if (!MachineByHand.ContainsKey(type.Name) && !MachineFindings(type).Any())
                    wrong.Add($"  {type.Name}: makine kaynağına ulaşmıyor; Machine'den çıkarın (ya da nedenini MachineByHand'e yazın)");
            }
            foreach (string name in MachineByHand.Keys)
                if (!classes.Any(t => t.Name == name && CollectionOf(t) == MachineCollection.Name))
                    wrong.Add($"  {name}: MachineByHand'de ama Machine'de değil");
            Assert.True(wrong.Count == 0, "Machine koleksiyonu:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
        }

        // Tarayıcının kendisi: doğrudan atama, lambda içinden okuma, async koddan ürün koduna inen yazma, ortam
        // değişkeni ve gerçek süreç görülür (örnekler hiç çalıştırılmaz)
        [Fact]
        public void Scanner_SeesDirectAndIndirectAccess()
        {
            List<StaticAccessScanner.Finding> findings = StaticAccessScanner.Scan(typeof(StaticAccessSamples));
            bool Has(StaticAccessScanner.Kind kind, string member) => findings.Any(f => f.Kind == kind && f.Member == member);

            Assert.True(Has(StaticAccessScanner.Kind.Write, StaticAccessSamples.Seam), string.Join(Environment.NewLine, findings));
            Assert.True(Has(StaticAccessScanner.Kind.Read, StaticAccessSamples.ReadInLambda), string.Join(Environment.NewLine, findings));
            Assert.True(Has(StaticAccessScanner.Kind.Write, "Environment.SetEnvironmentVariable"), string.Join(Environment.NewLine, findings));
            Assert.True(Has(StaticAccessScanner.Kind.Machine, "Process.Start"), string.Join(Environment.NewLine, findings));
#if !NETFRAMEWORK
            StaticAccessScanner.Finding deep = findings.SingleOrDefault(f => f.Kind == StaticAccessScanner.Kind.Write && f.Member == "POpsHelpers.MachineLogDir");
            Assert.NotNull(deep);
            Assert.Contains("AgentDirectories.Use", deep.Path);
#endif
            // İçeriği değişmeyen readonly koleksiyonlar ve sabit gibi kullanılan ayarlar sayılmaz
            Assert.DoesNotContain(findings, f => f.Member == "POpsHelpers.UserSessionComponents");
        }
    }

    // Tarayıcının sınaması için örnekler. Hiçbiri çalıştırılmaz; test sınıfı da değildir (yalnızca IL'leri okunur).
    internal static class StaticAccessSamples
    {
#if NETFRAMEWORK
        // MSI custom action'larının servis hesabı: TestEnvironment bir kez yazar
        public const string Seam = "Setup.SystemSid";
        public const string ReadInLambda = "Setup.SystemSid";

        public static void WritesASeam() => POps.Installer.Setup.SystemSid = null;

        public static Func<System.Security.Principal.SecurityIdentifier> ReadsInALambda() => () => POps.Installer.Setup.SystemSid;
#else
        // Süreç geneli kalan değerler: servis hesabı ve log klasörü (TestEnvironment bir kez yazar)
        public const string Seam = "SecureStore.SystemSid";
        public const string ReadInLambda = "POpsHelpers.LogDirectoryOverride";

        public static void WritesASeam() => POpsAgent.SecureStore.SystemSid = null;

        public static Func<string> ReadsInALambda() => () => POps.Shared.POpsHelpers.LogDirectoryOverride;

        // AgentDirectories.Use, SYSTEM bileşenlerinin log klasörünü süreç geneli statiğe yazar (POpsHelpers.MachineLogDir;
        // servis bir kez yazar, statik kalır)
        public static async Task ReachesProductStateFromAsyncCode()
        {
            await Task.Yield();
            POpsAgent.AgentDirectories.Use(null);
        }

        public static bool LogsToUserProfile() => POps.Shared.POpsHelpers.LogsToUserProfile;
#endif

        public static void SetsAnEnvironmentVariable() => Environment.SetEnvironmentVariable("POPS_SCANNER_SAMPLE", null);

        public static void StartsAProcess() => System.Diagnostics.Process.Start("cmd.exe", "/c exit").Dispose();
    }
}
