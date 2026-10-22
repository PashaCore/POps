using System;
using System.Runtime.Versioning;

namespace POpsAgent
{
    // Bir Worker'ın değişebilir durumu ve makineye açılan kapıları tek yerde. Servis için Program bir kez kurar (gerçek
    // klasörler ve makine), testler her test için kurar (geçici klasörler ve sahteler; bkz. POps.Tests.AgentHarness). Worker
    // bunu kurucusunda alır ve kurduğu servislere ve işleyicilere yalnızca kullandıkları parçayı verir, bağlamın tamamını
    // değil. İki Worker aynı süreçte birbirinin durumunu değiştiremez.
    //
    // Worker bölmesinin (b) adımları statikleri buraya taşır (bkz. docs/design/worker-split.md):
    //  * b1: yollar (Paths).
    //  * b2: çalışma durumu: yetenekler, modüller, kimlik bilgileri, ServerTrust, AgentHttp, güncelleme durumu, denetim
    //    izi ve saat (IAuditSink, TimeProvider).
    //  * b3: makine kapıları (AgentMachine: PowerShell çalıştırıcısı, kiosk kaydı, BITS, updater'ın başlatılması, boru
    //    seçenekleri, winget, ...).
    //  * b4: DnsPolicyMonitor.
    // Her yeni parça kurucuya bir argüman ve bir salt okunur özellik olarak eklenir.
    [SupportedOSPlatform("windows")]
    public sealed class AgentContext
    {
        public AgentContext(AgentPaths paths)
        {
            Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        }

        // Veri, güvenli depo, log, ayar dosyaları ve kurum sertifikası
        public AgentPaths Paths { get; }
    }
}
