using Xunit;

// Testler paralel çalışır: her test sınıfı kendi koleksiyonudur (xUnit varsayılanı), aynı anda en çok işlemci sayısı kadar
// test çalışır. Aşağıdaki iki koleksiyon seri kalır. xUnit 2.9, DisableParallelization = true olan koleksiyonları bütün
// paralel koleksiyonlar bittikten SONRA ve birbiri ardına çalıştırır: seri sınıflar ne paralel sınıflarla ne de
// birbirleriyle aynı anda çalışır. Bir sınıfın hangi koleksiyonda olduğunu ParallelSafetyTests denetler.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass, DisableTestParallelization = false)]

namespace POps.Tests
{
    // Statik bir duruma (yollar, test seam'leri, bellekteki ajan durumu, ortam değişkenleri) dokunan sınıflar. Hepsi
    // SharedStateTestBase'ten türer: EnsureIsolated yalnızca bu koleksiyonda, her testten önce çalışır. Worker bölmesinin
    // (b) adımları statikleri kaldırdıkça sınıflar buradan çıkar; (d) adımında koleksiyon boşalır ve silinir.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class SharedStateCollection
    {
        public const string Name = "SharedState";
    }

    // Kod yüzünden değil makine yüzünden seri olan sınıflar: gerçek süreç başlatan, adlandırılmış boru ya da soket açan,
    // ya da geçen gerçek süreyi ölçen testler. Yük altındaki bir makinede süre ölçümleri kayar; bunlar paralel testler
    // bittikten sonra, boş bir makinede çalışır. Statiklere dokunmazlar.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class MachineCollection
    {
        public const string Name = "Machine";
    }
}
