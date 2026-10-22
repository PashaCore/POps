using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading.Tasks;
using POps.Shared;

namespace POpsAgent
{
    // Ajanın makineye açılan kapıları: güvenlik duvarını, kayıt defteri politikalarını, sistem ortam değişkenlerini, BITS
    // işlerini, güç durumunu, kurulu yazılımı ve süreçleri değiştiren ya da bütün arabirimlerde dinleyen her şey, ve servisin
    // konsol oturumu sorguları.
    // Bir Worker'ın kapıları bağlamdadır (AgentContext.Machine); Worker kurduğu parçalara yalnızca kullandıkları kapıyı verir.
    //
    // Güvenli varsayılan: parametresiz kurulan nesnenin her kapısı reddeden ya da hiçbir şey yapmayan sahtedir (Refusing;
    // kiosk politikaları yalnızca bellekte tutulur, eş önbelleği dinlemez). Gerçek kapılar yalnızca Production()'da kurulur ve
    // onu yalnızca Program çağırır. Testler bağlamı AgentHarness ile kurar: kapı vermeyen her yol reddeden sahteye düşer, gerçek
    // güvenlik duvarına, kayıt defterine, sistem ortam değişkenlerine, BITS'e, güç API'sine, winget'e, süreçlere, updater'a ve
    // dinleyen bir sokete hiçbir yoldan ulaşılamaz (bkz. POps.Tests MachineSeamsTests). Bir test yalnızca sınadığı kapının
    // kendi sahtesini verir (init).
    [SupportedOSPlatform("windows")]
    public sealed class MachineSeams
    {
        // Güvenlik duvarı betikleri: ağ karantinası ve sınav modunun kural grupları (bkz. NetworkIsolation, ExamMode).
        // Dönen: çıkış kodu ve çıktı
        public Func<string, Task<(int Exit, string Output)>> FirewallScripts { get; init; } = Refusing.Script;
        // Karantinanın Ctrl+Alt+Del politikaları (HKLM ve kullanıcı kovanları; bkz. KioskMode)
        public IKioskRegistry Kiosk { get; init; } = new MemoryKioskRegistry();
        // Eş önbelleğinin güvenlik duvarı kuralı ve sunucusu (servis: TCP 8817'de bütün arabirimlerde dinleyen sunucu; bkz.
        // PeerCache, PeerCacheServer)
        public Func<string, Task<(int Exit, string Output)>> PeerCacheFirewall { get; init; } = Refusing.Script;
        public PeerCacheServerFactory PeerCacheServer { get; init; } = Refusing.NoPeerCacheServer;
        // Güç işlemi (kapatma, yeniden başlatma, oturum kapatma, kilitleme; bkz. PowerActions). Dönen: işlemin gerçek sonucu
        // (Win32 hata kodu ya da shutdown.exe'nin çıkış kodu; bkz. PowerOutcome)
        public Func<PowerOperation, int, PowerOutcome> Power { get; init; } = Refusing.Power;
        // Etkin konsol oturumunda oturum açmış kullanıcı var mı, ve o oturumun kimliği (bkz. SessionTasks)
        public Func<bool> HasConsoleUser { get; init; } = Refusing.NoConsoleUser;
        public Func<uint> ConsoleSession { get; init; } = Refusing.NoConsoleSession;
        // winget.exe'nin yeri (null: yok; bkz. WingetInstall)
        public Func<string?> WingetLocator { get; init; } = Refusing.NoWinget;
        // Sınav modu: izin listesindeki alan adlarının çözümü ve engelli uygulamanın kapatılması (bkz. ExamMode, ExamHandler)
        public Func<string, Task<IPAddress[]>> ExamResolver { get; init; } = Refusing.NoAddresses;
        public Action<Process> StopProcess { get; init; } = Refusing.KeepProcess;
        // Dosya aktarımı: ortak masaüstü ve kullanıcı profilleri (profil kökü, konsoldaki kullanıcının profili; bkz. FileTransfer)
        public Func<string> PublicDesktop { get; init; } = Refusing.NoFolder;
        public Func<(string ProfilesDirectory, string? CurrentProfile)> Profiles { get; init; } = Refusing.NoCurrentProfile;
        // Güncelleme paketinin BITS ile indirilmesi (betik, süre; null: BITS kullanılmaz, HttpClient ile indirilir) ve
        // updater'ın başlatılması (bkz. BitsDownload, AgentUpdater)
        public Func<string, TimeSpan, Task<(int Exit, string Output)>>? Bits { get; init; }
        public UpdaterLaunch LaunchUpdater { get; init; } = Refusing.Updater;
        // Tepsi borusunun adı ve bağlanan istemcinin denetimi (bkz. TrayPipeServer)
        public TrayPipeOptions TrayPipe { get; init; } = TrayPipeOptions.Unique();
        // --generalize: yönetici mi, servisi ve watchdog'u durdur (başarıda null, aksi halde neden; bkz. Generalizer)
        public Func<bool> IsAdministrator { get; init; } = Refusing.NotAdministrator;
        public Func<string?> StopAgent { get; init; } = Refusing.AgentNotStopped;
        // Sistem ortam değişkenleri (makine kapsamı, kayıt defterinde): eski sürümlerin gizli değer yazdığı değişkenin
        // okunması ve güvenli depoya taşındıktan sonra silinmesi (bkz. CredentialStore.MigrateSecrets)
        public Func<string, string?> ReadMachineVariable { get; init; } = Refusing.NoMachineVariable;
        public Action<string> DeleteMachineVariable { get; init; } = Refusing.KeepMachineVariable;

        // Servisin gerçek makinesi. Yalnızca Program çağırır (testler bağlamı AgentHarness ile kurar).
        internal static MachineSeams Production() => new MachineSeams
        {
            FirewallScripts = NetworkIsolation.RunPowerShellAsync,
            Kiosk = new WindowsKioskRegistry(),
            PeerCacheFirewall = PeerCache.RunPowerShellAsync,
            PeerCacheServer = (sha256, path, isLocal, maxClients) =>
                new PeerCacheServer(sha256, path, new IPEndPoint(IPAddress.IPv6Any, PeerCache.Port), isLocal, maxClients),
            Power = WindowsPower.Run,
            HasConsoleUser = SessionTasks.ConsoleUserPresent,
            ConsoleSession = () => UserSessionLauncher.ActiveConsoleSession(),
            WingetLocator = WingetInstall.LocateWinget,
            ExamResolver = host => Dns.GetHostAddressesAsync(host),
            StopProcess = process => process.Kill(),
            PublicDesktop = () => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Profiles = FileTransfer.ReadProfiles,
            Bits = BitsDownload.RunPowerShellAsync,
            LaunchUpdater = AgentUpdater.StartUpdater,
            TrayPipe = TrayPipeOptions.Installed(),
            IsAdministrator = () => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            StopAgent = Generalizer.StopServiceAndWatchdog,
            ReadMachineVariable = name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine),
            DeleteMachineVariable = name => Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Machine),
        };

        // Kapı verilmeyen her yolun sahteleri: makineye dokunmaz, reddeder ya da "yok" der
        internal static class Refusing
        {
            public const string ScriptOutput = "testlerde güvenlik duvarına dokunulmaz";

            public static Task<(int Exit, string Output)> Script(string script) => Task.FromResult((1, ScriptOutput));

            // Eş önbelleği dinlemez: sunucu kurulur ama Start, portu açılamayan bir sunucu gibi SocketException atar (PeerCache
            // bunu loglar; paket sunulmaz, güvenlik duvarı kuralı eklenmez). Hiçbir yoldan soket açılmaz.
            public static IPeerCacheServer NoPeerCacheServer(string sha256, string path, Func<IPAddress, bool> isLocal, int maxClients) =>
                new NotListeningPeerCacheServer(sha256);

            public static PowerOutcome Power(PowerOperation operation, int taskId) =>
                throw new InvalidOperationException($"Testte gerçek güç API'si çağrıldı ({operation}, görev {taskId}).");

            public static bool NoConsoleUser() => false;

            public static uint NoConsoleSession() => UserSessionLauncher.NoSession;

            public static string? NoWinget() => null;

            public static Task<IPAddress[]> NoAddresses(string host) => Task.FromResult(Array.Empty<IPAddress>());

            // Bu bilgisayardaki gerçek süreçler (cmd.exe, powershell.exe) kapatılmaz
            public static void KeepProcess(Process process) { }

            // Klasör yok: ortak masaüstüne gönderim "hedef klasör yok" ile biter
            public static string NoFolder() => "";

            // Konsolda kullanıcı yok: her profil başka bir kullanıcınındır (any_profile olmadan alınmaz)
            public static (string ProfilesDirectory, string? CurrentProfile) NoCurrentProfile() =>
                (Environment.ExpandEnvironmentVariables(@"%SystemDrive%\Users"), null);

            public static bool Updater(AgentPaths paths, string fromVersion, string package, string sha256, string toVersion, Action<string> reject)
            {
                reject("POpsUpdater başlatılmadı (testlerde updater çalıştırılmaz)");
                return false;
            }

            public static bool NotAdministrator() => false;

            public static string? AgentNotStopped() => "testlerde servis ve watchdog durdurulmaz";

            // Bu bilgisayarın sistem ortam değişkenleri okunmaz ve silinmez: eski sürümden kalan gizli değer yok sayılır
            public static string? NoMachineVariable(string name) => null;

            public static void KeepMachineVariable(string name) { }
        }

        // Hiç dinlemeyen eş önbelleği sunucusu (Refusing.NoPeerCacheServer)
        private sealed class NotListeningPeerCacheServer : IPeerCacheServer
        {
            public NotListeningPeerCacheServer(string sha256) => Sha256 = sha256;

            public string Sha256 { get; }
            public int Port => 0;
            public int ActiveClients => 0;

            public void Start() => throw new SocketException((int)SocketError.AccessDenied);

            public Task StopAsync() => Task.CompletedTask;

            public void Dispose() { }
        }

        // Bellekteki kiosk kaydı: gerçek HKLM/HKU politikalarına dokunulmaz
        private sealed class MemoryKioskRegistry : IKioskRegistry
        {
            private readonly Dictionary<string, int> _values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            private static string PathOf(string hive, string key, string name) => Path.Combine(hive, key, name);

            public IList<string> UserHives() => new List<string>();

            public int? Get(string hive, string key, string name)
            {
                lock (_values) return _values.TryGetValue(PathOf(hive, key, name), out int value) ? value : null;
            }

            public void Set(string hive, string key, string name, int value)
            {
                lock (_values) _values[PathOf(hive, key, name)] = value;
            }

            public void Delete(string hive, string key, string name)
            {
                lock (_values) _values.Remove(PathOf(hive, key, name));
            }
        }
    }
}
