using System;
using System.IO;

namespace POps.Shared
{
    // Geri dönüş tatbikatı işaretleri (C:\POpsData\secure içinde; yalnızca SYSTEM ve Administrators yazabilir).
    //  * rollback-drill: yöneticinin koyduğu işaret.
    //  * rollback-drill.consumed: güncellemeyle kurulan yeni sürümün ajanı işareti ilk açılışında buna çevirir ve
    //    içine kendi sürümünü ve güncelleme çalışmasının başlangıcını yazar. Böylece geri kurulan eski sürüm (işareti
    //    bilmese bile) işareti görmez ve sağlık bildirir.
    // Updater yeni sürümü sağlıksız sayıp geri dönmeye karar verince (geri kurulumdan ÖNCE) ve iş sonunda ikisini de siler.
    public static class RollbackDrill
    {
        public const string MarkerFileName = "rollback-drill";
        public const string ConsumedFileName = "rollback-drill.consumed";

        // Dönen: silinen dosya sayısı. Silinemeyen dosya loglanır.
        public static int Clear(string secureDir, Action<string, bool> log)
        {
            int removed = 0;
            foreach (string name in new[] { MarkerFileName, ConsumedFileName })
            {
                string path = Path.Combine(secureDir, name);
                try
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) { log?.Invoke($"[TATBİKAT] {path} silinemedi: {ex.Message}", true); }
            }
            return removed;
        }
    }
}
