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
    //  * b2: çalışma durumu: yetenekler, modüller, kimlik bilgileri, ServerTrust, AgentHttp, güncelleme durumu.
    //  * b4: DnsPolicyMonitor.
    //  * b3: makine kapıları (AgentMachine: PowerShell çalıştırıcısı, kiosk kaydı, BITS, boru seçenekleri, winget, ...).
    // Her yeni parça kurucuya bir argüman ve bir salt okunur özellik olarak eklenir.
    [SupportedOSPlatform("windows")]
    public sealed class AgentContext
    {
        public AgentContext(AgentPaths paths, CapabilityState capabilities, ModuleState modules, CredentialStore credentials,
            ServerTrust trust, AgentHttp http, AgentUpdater updater)
        {
            Paths = paths ?? throw new ArgumentNullException(nameof(paths));
            Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
            Modules = modules ?? throw new ArgumentNullException(nameof(modules));
            Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            Trust = trust ?? throw new ArgumentNullException(nameof(trust));
            Http = http ?? throw new ArgumentNullException(nameof(http));
            Updater = updater ?? throw new ArgumentNullException(nameof(updater));
        }

        // Veri, güvenli depo, log, ayar dosyaları ve kurum sertifikası
        public AgentPaths Paths { get; }
        // Yerel yetenek kilidi (capabilities.json; bkz. AgentCapabilities)
        public CapabilityState Capabilities { get; }
        // Sunucunun laboratuvar bazında kapattığı modüller (bellekte; bkz. AgentModules)
        public ModuleState Modules { get; }
        // Cihaz secret'ı, enroll jetonu, bypass anahtarları (bkz. AgentCredentials)
        public CredentialStore Credentials { get; }
        // Sunucu sertifikasına güven (server-ca.pem; bkz. ServerTrust)
        public ServerTrust Trust { get; }
        // Ajan HTTP istemcisi ve bildirimleri (bkz. AgentHttp)
        public AgentHttp Http { get; }
        // Kurulu sürüm, güncelleme emri ve tatbikat (bkz. AgentUpdate)
        public AgentUpdater Updater { get; }

        // Servis açılışında, Program'dan bir kez: Worker bölmesinin b3/b4 adımlarına kadar statik kalan kodun okuduğu süreç
        // varsayılanları bu bağlamın örneklerine çevrilir (PeerCache ve X-Agent-Features: yetenekler; FileTransfer ve
        // DnsPolicyMonitor: HTTP istemcisi ve cihaz secret'ı). Böylece serviste her parçanın tek örneği vardır.
        internal void UseForProcess()
        {
            AgentCapabilities.Process = Capabilities;
            AgentCredentials.Process = Credentials;
            AgentHttp.Default = Http;
        }
    }
}
