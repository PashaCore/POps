using System;
using System.Linq;
using System.Runtime.Versioning;

namespace POpsAgent
{
    // X-Agent-Features: /ws/agent bağlantısında duyurulan, ajanın uyguladığı özellikler (virgülle ayrılmış, [a-z0-9_]).
    // Sunucu bu eylemleri yalnızca duyuran ajana gönderir; bilmeyen sunucu başlığı yok sayar.
    // peer_cache yalnızca peer_cache_enabled açıkken duyurulur: sunucu duyuran PC'yi sınıfın tohumu ya da eşi seçebilir,
    // kapalı yetenekle paket tutulmaz ve sunulmaz (bkz. PeerCache). Başlık bağlamın yetenek durumundan her bağlantıda
    // yeniden üretilir (bkz. CommandConnection).
    [SupportedOSPlatform("windows")]
    public sealed class AgentFeatures
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

        private readonly CapabilityState _capabilities;

        // capabilities: bağlamın yetenek durumu (peer_cache_enabled)
        public AgentFeatures(CapabilityState capabilities)
        {
            _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        }

        public string Header => string.Join(",", All.Where(feature => feature != PeerCache || _capabilities.PeerCacheEnabled));
    }
}
