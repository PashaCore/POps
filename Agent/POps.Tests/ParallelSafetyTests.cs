using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace POps.Tests
{
    // Paralel çalışmanın kuralı. Değişebilir bir statiğe (yol ayarları, test seam'leri, bellekteki ajan durumu, ortam
    // değişkenleri) yalnızca seri SharedState koleksiyonundaki sınıflar dokunur; gerçek süreç başlatan, adlandırılmış boru
    // ya da dinleyen soket açan sınıflar da seridir (Machine). Bir sınıfın nelere ulaştığı, kendi kodundan ve çağırdığı
    // ürün kodundan IL ile bulunur (StaticAccessScanner; sınırları orada yazılı). Süre ölçen testler IL'den anlaşılmaz:
    // onlar elle Machine'e konur.
    public class ParallelSafetyTests : TestBase
    {
        // Paralel sınıfların da okuyabildiği statikler: test süreci başında TestEnvironment bir kez kurar; değiştiren testler
        // SharedState'tedir ve o koleksiyon paralel sınıflar bittikten sonra çalışır. Yazmak yine yalnızca SharedState'te.
        private static readonly Dictionary<string, string> ReadAnywhere = new Dictionary<string, string>
        {
            ["POpsHelpers.LogDirectoryOverride"] = "bütün testlerin tek log klasörü; POpsHelpers.Log kilit altında yazar",
            ["POpsHelpers.MachineLogDir"] = "POpsHelpers.Log okur; testlerde LogDirectoryOverride dolu olduğu için kullanılmaz",
            ["POpsHelpers.Component"] = "programın adı, programda bir kez atanır; değiştiren POpsHelpersTests SharedState'te",
            ["FolderSettings.SystemSid"] = "servis hesabı (testte test kullanıcısı); korumalı dosya ve klasör ACL'leri okur",
            ["SecureStore.SystemSid"] = "servis hesabı (testte test kullanıcısı); değiştiren SecureStoreTests SharedState'te",
            ["Setup.SystemSid"] = "MSI custom action'larının servis hesabı (testte test kullanıcısı)",
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

        // SharedStateTestBase'in kurucusu (EnsureIsolated) sınıfın kendi erişimi sayılmaz: o kurucu yalnızca SharedState'te
        // çalışır (SharedStateBase_AndCollection_GoTogether)
        private static List<StaticAccessScanner.Finding> Scan(Type type) =>
            StaticAccessScanner.Scan(type, method => method.DeclaringType == typeof(SharedStateTestBase));

        private static IEnumerable<StaticAccessScanner.Finding> StateFindings(Type type) =>
            Scan(type).Where(f => f.Kind != StaticAccessScanner.Kind.Machine
                && !(f.Kind == StaticAccessScanner.Kind.Read && ReadAnywhere.ContainsKey(f.Member)));

        private static string Report(IEnumerable<(Type Type, StaticAccessScanner.Finding Finding)> findings) =>
            string.Join(Environment.NewLine, findings.Select(x => $"  {x.Type.Name}: {x.Finding}"));

        [Fact]
        public void OnlySharedStateClasses_TouchMutableStatics()
        {
            var findings = TestClasses()
                .Where(t => CollectionOf(t) != SharedStateCollection.Name)
                .SelectMany(t => StateFindings(t).Select(f => (t, f)))
                .ToList();
            Assert.True(findings.Count == 0,
                "Bu sınıflar paralel (ya da Machine) ama değişebilir bir statiğe dokunuyor. Statiği kaldırın ya da sınıfı " +
                "[Collection(SharedStateCollection.Name)] ile işaretleyip SharedStateTestBase'ten türetin:" + Environment.NewLine + Report(findings));
        }

        [Fact]
        public void ParallelClasses_UseNoMachineResources()
        {
            var findings = TestClasses()
                .Where(t => CollectionOf(t) == null)
                .SelectMany(t => Scan(t).Where(f => f.Kind == StaticAccessScanner.Kind.Machine).Select(f => (t, f)))
                .ToList();
            Assert.True(findings.Count == 0,
                "Bu sınıflar paralel ama gerçek süreç, adlandırılmış boru, soket ya da Thread.Sleep kullanıyor. Sınıfı " +
                "[Collection(MachineCollection.Name)] ile işaretleyin (statiğe de dokunuyorsa SharedState):" + Environment.NewLine + Report(findings));
        }

        // EnsureIsolated yalnızca SharedState'te çalışır: SharedStateTestBase'ten türeyen her sınıf o koleksiyonda, o
        // koleksiyondaki her sınıf ondan türer; başka koleksiyon adı kullanılmaz
        [Fact]
        public void SharedStateBase_AndCollection_GoTogether()
        {
            var wrong = new List<string>();
            foreach (Type type in TestClasses())
            {
                string collection = CollectionOf(type);
                bool sharedBase = typeof(SharedStateTestBase).IsAssignableFrom(type);
                if (collection != null && collection != SharedStateCollection.Name && collection != MachineCollection.Name)
                    wrong.Add($"  {type.Name}: bilinmeyen koleksiyon \"{collection}\"");
                else if (sharedBase != (collection == SharedStateCollection.Name))
                    wrong.Add($"  {type.Name}: koleksiyon {collection ?? "(paralel)"}, SharedStateTestBase'ten türüyor: {sharedBase}");
            }
            Assert.True(wrong.Count == 0, "SharedState sınıfları SharedStateTestBase'ten türer, diğerleri türemez:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
        }

        // Worker bölmesinin (b) adımları statikleri kaldırdıkça SharedState sınıfları serbest kalır: artık hiçbir statiğe
        // dokunmayan sınıf koleksiyondan çıkarılır (ve TestBase'ten türer), koleksiyon böylece yalnızca küçülür
        [Fact]
        public void SharedStateClasses_StillTouchAStatic()
        {
            var free = TestClasses()
                .Where(t => CollectionOf(t) == SharedStateCollection.Name && !StateFindings(t).Any())
                .Select(t => "  " + t.Name)
                .ToList();
            Assert.True(free.Count == 0,
                "Bu sınıflar artık değişebilir bir statiğe dokunmuyor: SharedState'ten çıkarın (TestBase, koleksiyon yok; " +
                "gerçek süreç, boru ya da soket kullanıyorsa Machine):" + Environment.NewLine + string.Join(Environment.NewLine, free));
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
            StaticAccessScanner.Finding deep = findings.SingleOrDefault(f => f.Kind == StaticAccessScanner.Kind.Write && f.Member == "AgentUpdate._auditedResult");
            Assert.NotNull(deep);
            Assert.Contains("AgentUpdate.PendingResultAudit", deep.Path);
#endif
            // İçeriği değişmeyen readonly koleksiyonlar ve sabit gibi kullanılan ayarlar sayılmaz
            Assert.DoesNotContain(findings, f => f.Member == "POpsHelpers.UserSessionComponents");
        }
    }

    // Tarayıcının sınaması için örnekler. Hiçbiri çalıştırılmaz; test sınıfı da değildir (yalnızca IL'leri okunur).
    internal static class StaticAccessSamples
    {
#if NETFRAMEWORK
        public const string Seam = "Setup.TrustedBaseForTests";
        public const string ReadInLambda = "Setup.TrustedBaseForTests";

        public static void WritesASeam() => POps.Installer.Setup.TrustedBaseForTests = null;

        public static Func<string> ReadsInALambda() => () => POps.Installer.Setup.TrustedBaseForTests;
#else
        public const string Seam = "SecureStore.Dir";
        public const string ReadInLambda = "AgentUpdate.DataDir";

        public static void WritesASeam() => POpsAgent.SecureStore.Dir = null;

        public static Func<string> ReadsInALambda() => () => POpsAgent.AgentUpdate.DataDir;

        // AgentUpdate.PendingResultAudit, sonucun denetim kaydına yazıldığını statik bir alanda tutar
        public static async Task ReachesProductStateFromAsyncCode()
        {
            await Task.Yield();
            POpsAgent.AgentUpdate.PendingResultAudit(null);
        }

        public static bool LogsToUserProfile() => POps.Shared.POpsHelpers.LogsToUserProfile;
#endif

        public static void SetsAnEnvironmentVariable() => Environment.SetEnvironmentVariable("POPS_SCANNER_SAMPLE", null);

        public static void StartsAProcess() => System.Diagnostics.Process.Start("cmd.exe", "/c exit").Dispose();
    }
}
