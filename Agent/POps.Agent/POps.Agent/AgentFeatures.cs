using System.Linq;

namespace POpsAgent
{
    // X-Agent-Features: /ws/agent bağlantısında duyurulan, ajanın uyguladığı özellikler (virgülle ayrılmış, [a-z0-9_]).
    // Sunucu bu eylemleri yalnızca duyuran ajana gönderir; bilmeyen sunucu başlığı yok sayar.
    // peer_cache yalnızca peer_cache_enabled açıkken duyurulur: sunucu duyuran PC'yi sınıfın tohumu ya da eşi seçebilir,
    // kapalı yetenekle paket tutulmaz ve sunulmaz (bkz. PeerCache).
    public static class AgentFeatures
    {
        public const string HeaderName = "X-Agent-Features";
        public const string PeerCache = "peer_cache";

        public static readonly string[] All =
        {
            "exam",
            "files",
            "winget",
            "power",
            "message",
            PeerCache,
        };

        public static string Header => string.Join(",", All.Where(feature => feature != PeerCache || AgentCapabilities.PeerCacheEnabled));
    }
}
