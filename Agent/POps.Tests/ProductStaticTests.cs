#if !NETFRAMEWORK
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace POps.Tests
{
    // Ürünün statik durumu (POpsAgent ve POps.Shared). Değişebilir her statik, yani statik kurucu dışında yazılan ya da içeriği
    // değişen statik alan ve set erişimcisi olan her statik özellik (kimse atamasa da), aşağıdaki listede bir gerekçeyle yer
    // alır. Yeni bir statik böylece bir test seam'i olarak sessizce giremez: ya bir nesnenin kurucu argümanına (AgentContext,
    // MachineSeams, AgentHarness) dönüşür ya da neden süreç geneli olduğu incelemede tartışılır. Liste yalnızca küçülür: artık
    // değişmeyen bir statiğin satırı da silinir. Tarama StaticAccessScanner'ın kuralıyladır (MutableStatics): derleyicinin
    // ürettiği türler, kapsam ölçümünün sayaç türleri, sabitler, [ThreadStatic] alanlar ve içeriği değişmeyen readonly alanlar
    // (ör. makine kapılarının Gate semaforları) sayılmaz.
    public class ProductStaticTests : TestBase
    {
        private static readonly Dictionary<string, string> Allowed = new Dictionary<string, string>
        {
            ["POpsHelpers.MachineLogDir"] = "SYSTEM bileşenlerinin log klasörü, süreç başına bir kez atanır: servis AgentPaths.LogDir'den "
                + "(AgentDirectories.Use), updater --logdir'den; POpsHelpers.Log okur (testlerde TestEnvironment bir kez atar)",
            ["POpsHelpers.Component"] = "bileşenin adı (log dosyasının adı ve klasörü): her program açılışta bir kez atar, ajan varsayılanı "
                + "\"Agent\"tır",
            ["POpsHelpers.LogDirectoryOverride"] = "testlerin log klasörü (internal): TestEnvironment test süreci başında bir kez atar, "
                + "ürün atamaz",
            ["FolderSettings.SystemSid"] = "servis hesabı (LocalSystem), veri ve log klasörlerinin ACL'i: süreç boyunca tek değer "
                + "(testlerde TestEnvironment bir kez test kullanıcısına çevirir)",
            ["SecureStore.SystemSid"] = "servis hesabı (LocalSystem), güvenli deponun ACL'i ve sahip denetimi: süreç boyunca tek değer "
                + "(testlerde TestEnvironment bir kez test kullanıcısına çevirir; başka hesapla denetleyen test argüman verir)",
            ["Authenticode.Cache"] = "dosyanın imza sonucu (yol ve son değişme zamanıyla): sonuç dosyanın kendisine bağlıdır, iki ajan "
                + "aynı dosya için aynı sonucu alır; tepsi istemcisi her bağlandığında WinVerifyTrust yeniden çalışmasın",
        };

        private static readonly Assembly[] Product = { typeof(POpsAgent.Worker).Assembly, typeof(POps.Shared.POpsHelpers).Assembly };

        // Listede olmayan değişebilir statikler ve listede olup artık bulunmayan satırlar
        private static (List<string> Unlisted, List<string> Stale) Check(IEnumerable<Assembly> assemblies, IReadOnlyDictionary<string, string> allowed)
        {
            var found = new SortedSet<string>(assemblies.SelectMany(StaticAccessScanner.MutableStatics), StringComparer.Ordinal);
            return (found.Where(m => !allowed.ContainsKey(m)).ToList(),
                allowed.Keys.Where(m => !found.Contains(m)).OrderBy(m => m, StringComparer.Ordinal).ToList());
        }

        [Fact]
        public void ProductStatics_AreOnTheAllowList()
        {
            (List<string> unlisted, List<string> stale) = Check(Product, Allowed);
            Assert.True(unlisted.Count == 0,
                "POpsAgent ya da POps.Shared'da listede olmayan değişebilir statik. Değeri bir nesneye taşıyın (kurucu argümanı; testte "
                + "AgentHarness ya da MachineSeams), süreç geneli olması gerekiyorsa ProductStaticTests.Allowed'a gerekçesiyle yazın:"
                + Environment.NewLine + string.Join(Environment.NewLine, unlisted.Select(m => "  " + m)));
            Assert.True(stale.Count == 0,
                "Listede ama artık değişebilir bir statik değil (ya da yok): satırı silin" + Environment.NewLine
                + string.Join(Environment.NewLine, stale.Select(m => "  " + m)));
            Assert.All(Allowed, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key + ": gerekçe yok"));
        }

        // Bekçinin kendisi: test derlemesindeki örnek statikler bildirilir; derleyicinin ürettiği türler, kapsam sayacı adındaki
        // tür, sabit, readonly değer, yalnızca statik kurucuda yazılan alan, [ThreadStatic] alan ve salt okunur özellik bildirilmez
        [Fact]
        public void Guard_ReportsASampleStatic()
        {
            (List<string> unlisted, List<string> stale) = Check(new[] { typeof(ProductStaticTests).Assembly },
                new Dictionary<string, string> { ["ProductStaticSamples.Seam"] = "örnek: listede", ["ProductStaticSamples.Gone"] = "örnek: yok" });

            Assert.Contains("ProductStaticSamples._calls", unlisted);
            Assert.Contains("ProductStaticSamples.Seen", unlisted);
            Assert.DoesNotContain("ProductStaticSamples.Seam", unlisted);
            Assert.Equal(new[] { "ProductStaticSamples.Gone" }, stale);
            Assert.Contains("ProductStaticSamples.Seam", StaticAccessScanner.MutableStatics(typeof(ProductStaticTests).Assembly));

            foreach (string quiet in new[] { "Constant", "Fixed", "_setOnce", "_perThread", "Count" })
                Assert.DoesNotContain("ProductStaticSamples." + quiet, unlisted);
            Assert.DoesNotContain(unlisted, m => m.StartsWith("<", StringComparison.Ordinal) || m.Contains("<>", StringComparison.Ordinal));
            Assert.DoesNotContain(unlisted, m => m.StartsWith("ProductStaticSamples_", StringComparison.Ordinal));
        }
    }

    // Bekçinin örnekleri. Hiçbiri çalıştırılmaz (yalnızca tanımları ve IL'leri okunur).
    internal static class ProductStaticSamples
    {
        // Bildirilir: set erişimcisi olan statik özellik (kimse atamasa da), statik kurucu dışında yazılan alan, içeriği değişen
        // readonly koleksiyon
        internal static Func<string>? Seam { get; set; }
        private static int _calls;
        private static readonly List<string> Seen = new List<string>();

        // Bildirilmez: sabit, readonly değer, yalnızca statik kurucuda yazılan alan, [ThreadStatic] alan, salt okunur özellik
        public const string Constant = "sabit";
        private static readonly string Fixed = "readonly";
        private static int _setOnce;
        [ThreadStatic] private static int _perThread;
        public static int Count => _setOnce;

        static ProductStaticSamples() => _setOnce = 1;

        public static void Use()
        {
            _calls++;
            _perThread++;
            Seen.Add(Fixed);
            // Yakalamayan lambda: derleyici <>c türünde önbelleğe alır, o tür sayılmaz
            Func<int> read = () => _setOnce;
            Seen.Add(read().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // Kapsam ölçümünün (coverlet) her derlemeye eklediği sayaç türü gibi adlandırılmış (<ad>_<guid>, statik HitsArray): sayılmaz
    internal static class ProductStaticSamples_6f9619ff8b86d011b42d00c04fc964ff
    {
        public static int[]? HitsArray;

        public static void RecordHit(int index)
        {
            HitsArray ??= new int[1];
            HitsArray[index]++;
        }
    }
}
#endif
