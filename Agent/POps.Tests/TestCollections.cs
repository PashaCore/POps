using Xunit;

// Testler paralel çalışır: her test sınıfı kendi koleksiyonudur (xUnit varsayılanı), aynı anda en çok işlemci sayısı kadar
// test çalışır (MaxParallelThreads xUnit'in varsayılanı). Aşağıdaki koleksiyon seri kalır: xUnit 2.9, DisableParallelization
// = true olan koleksiyonu bütün paralel koleksiyonlar bittikten SONRA çalıştırır; içindeki sınıflar ne paralel sınıflarla ne de
// birbirleriyle aynı anda çalışır. Değişebilir bir statiğe hiçbir test sınıfı dokunmaz (Worker bölmesinin (b) ve (d) adımları);
// bir sınıfın nelere ulaştığını ve hangi koleksiyonda olması gerektiğini ParallelSafetyTests denetler.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass, DisableTestParallelization = false)]

namespace POps.Tests
{
    // Kod yüzünden değil makine yüzünden seri olan sınıflar: gerçek süreç başlatan, adlandırılmış boru ya da dinleyen soket açan
    // (ya da onları açan koda IL'den ulaşan), ya da geçen gerçek süreyi ölçen testler. Yük altındaki bir makinede süre ölçümleri
    // kayar; bunlar paralel testler bittikten sonra, boş bir makinede çalışır. Statiklere dokunmazlar; her sınıf nedenini bir
    // yorumla söyler.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class MachineCollection
    {
        public const string Name = "Machine";
    }
}
