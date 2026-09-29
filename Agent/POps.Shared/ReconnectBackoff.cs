using System;

namespace POps.Shared
{
    // Ajanın komut tüneline yeniden bağlanma beklemesi: üstel geri çekilme + full jitter.
    //   bekleme = rastgele(0, min(60 sn, 2 sn × 2^deneme))
    // "deneme", son sağlam bağlantıdan beri art arda başarısız olan bağlantı sayısıdır (ilk kopuşta 0). 0.1.7'ye kadar
    // her ajan sabit 5 sn bekliyordu: sunucu yeniden başlayınca 500 ajan aynı anda, 5 sn'de bir bağlanmaya çalışıyordu
    // (BENCHMARKS.md). Rastgele bekleme ajanları zamana yayar, üst sınır kopuk bir ajanın dakikada bir denemesini sağlar.
    // Sunucu kimliği reddettiyse (4401) her deneme sunucuda denetim kaydı açar: en az 60 sn beklenir, üstüne aynı
    // jitter eklenir ki reddedilen ajanlar da aynı anda gelmesin.
    public static class ReconnectBackoff
    {
        public static readonly TimeSpan Base = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan AuthRejectedMinimum = TimeSpan.FromSeconds(60);

        // 2 sn × 2^5 = 64 sn tavanı zaten aşar; sayaç bundan sonra büyütülmez (taşma olmaz)
        public const int MaxAttempt = 5;

        // Bu denemenin tavanı: min(Cap, Base × 2^attempt)
        public static TimeSpan Ceiling(int attempt)
        {
            int exponent = Math.Clamp(attempt, 0, MaxAttempt);
            double ms = Math.Min(Cap.TotalMilliseconds, Base.TotalMilliseconds * (1 << exponent));
            return TimeSpan.FromMilliseconds(ms);
        }

        // random dışarıdan verilir (testte sabit değer, ajanda Random.Shared)
        public static TimeSpan Delay(int attempt, bool authRejected, Random random)
        {
            TimeSpan jitter = TimeSpan.FromMilliseconds(random.NextDouble() * Ceiling(attempt).TotalMilliseconds);
            return authRejected ? AuthRejectedMinimum + jitter : jitter;
        }

        public static int NextAttempt(int attempt) => Math.Clamp(attempt, 0, MaxAttempt - 1) + 1;
    }
}
