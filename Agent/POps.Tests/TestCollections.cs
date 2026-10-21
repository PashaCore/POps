using Xunit;

// Testler paralel çalışır: her test sınıfı kendi koleksiyonudur (xUnit varsayılanı), aynı anda en çok işlemci sayısı kadar
// test çalışır. Aşağıdaki iki koleksiyon seri kalır. xUnit 2.9, DisableParallelization = true olan koleksiyonları bütün
// paralel koleksiyonlar bittikten SONRA ve birbiri ardına çalıştırır: seri sınıflar ne paralel sınıflarla ne de
// birbirleriyle aynı anda çalışır. Bir sınıfın hangi koleksiyonda olduğunu ParallelSafetyTests denetler.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass, DisableTestParallelization = false)]

namespace POps.Tests
{
    // Süreç geneli bir değere (ortam değişkenleri, parametresiz ayar okumalarının dosyaları, servis hesabı, kendi statik
    // önbelleği, MSI custom action'larının güvenilir kökü) dokunan sınıflar. Hepsi SharedStateTestBase'ten türer. Worker
    // bölmesinin (b) adımları statikleri kaldırdıkça sınıflar buradan çıktı; (d) adımında kalanlar da çıkar, koleksiyon silinir.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class SharedStateCollection
    {
        public const string Name = "SharedState";
    }

    // Kod yüzünden değil makine yüzünden seri olan sınıflar: gerçek süreç başlatan, adlandırılmış boru ya da soket açan
    // (ya da onları açan koda IL'den ulaşan; ör. Worker'ın eş önbelleği sunucusu), ya da geçen gerçek süreyi ölçen
    // testler. Yük altındaki bir makinede süre ölçümleri kayar; bunlar paralel testler bittikten sonra, boş bir makinede
    // çalışır. Statiklere dokunmazlar; her sınıf nedenini bir yorumla söyler.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class MachineCollection
    {
        public const string Name = "Machine";
    }
}
