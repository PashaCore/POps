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
    //  * b4: DnsPolicyMonitor bir örnek; bağlamda değil, Worker'da (Worker.Dns): geri çağrıları Worker'ındır. HTTP istemcisini
    //    ve kimlik bilgilerini bağlamdan alır.
    //  * b3: makine kapıları (Machine: güvenlik duvarı betikleri, kiosk kaydı, eş önbelleği, BITS, updater, güç, oturum,
    //    winget, sınav, dosya aktarımı, tepsi borusu, --generalize; bkz. MachineSeams).
    //  * Saat (Clock): bilgisayarın saati ve monotonik saat; bugün sınav modunun bitişi okur (bkz. ExamMode).
    // Her yeni parça kurucuya bir argüman ve bir salt okunur özellik olarak eklenir.
    [SupportedOSPlatform("windows")]
    public sealed class AgentContext
    {
        public AgentContext(AgentPaths paths, CapabilityState capabilities, ModuleState modules, CredentialStore credentials,
            ServerTrust trust, AgentHttp http, AgentUpdater updater, MachineSeams machine, TimeProvider clock)
        {
            Paths = paths ?? throw new ArgumentNullException(nameof(paths));
            Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
            Modules = modules ?? throw new ArgumentNullException(nameof(modules));
            Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            Trust = trust ?? throw new ArgumentNullException(nameof(trust));
            Http = http ?? throw new ArgumentNullException(nameof(http));
            Updater = updater ?? throw new ArgumentNullException(nameof(updater));
            Machine = machine ?? throw new ArgumentNullException(nameof(machine));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        // Veri, güvenli depo, log, ayar dosyaları, kurum sertifikası ve görev dosyaları
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
        // Kurulu sürüm, güncelleme emri, tatbikat ve eş önbelleği (bkz. AgentUpdate)
        public AgentUpdater Updater { get; }
        // Makineye açılan kapılar (servis: MachineSeams.Production; testlerde reddeden sahteler)
        public MachineSeams Machine { get; }
        // Bilgisayarın saati (GetUtcNow) ve monotonik saat (GetTimestamp; saat ayarlansa da ileri gider). Servis: AgentClock;
        // testlerde ikisini ayrı ayrı ilerleten sahte.
        public TimeProvider Clock { get; }
    }
}
