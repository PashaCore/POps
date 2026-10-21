using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading.Tasks;
using POps.Shared;

namespace POpsAgent
{
    // Ajanın makineye açılan kapıları: güvenlik duvarını, kayıt defteri politikalarını, BITS işlerini, güç durumunu, kurulu
    // yazılımı ve süreçleri değiştiren ya da bütün arabirimlerde dinleyen her şey, ve servisin konsol oturumu sorguları.
    // Bir Worker'ın kapıları bağlamdadır (AgentContext.Machine); Worker kurduğu parçalara yalnızca kullandıkları kapıyı verir.
    //
    // Güvenli varsayılan: parametresiz kurulan nesnenin her kapısı reddeden ya da hiçbir şey yapmayan sahtedir (Refusing;
    // kiosk politikaları yalnızca bellekte tutulur). Gerçek kapılar yalnızca Production()'da kurulur ve onu yalnızca Program
    // çağırır. Testler bağlamı AgentHarness ile kurar: kapı vermeyen her yol reddeden sahteye düşer, gerçek güvenlik duvarına,
    // kayıt defterine, BITS'e, güç API'sine, winget'e, süreçlere ve updater'a hiçbir yoldan ulaşılamaz (bkz. POps.Tests
    // MachineSeamsTests). Bir test yalnızca sınadığı kapının kendi sahtesini verir (init).
    [SupportedOSPlatform("windows")]
    public sealed class MachineSeams
    {
        // Güvenlik duvarı betikleri: ağ karantinası ve sınav modunun kural grupları (bkz. NetworkIsolation, ExamMode).
        // Dönen: çıkış kodu ve çıktı
        public Func<string, Task<(int Exit, string Output)>> FirewallScripts { get; init; } = Refusing.Script;
        // Karantinanın Ctrl+Alt+Del politikaları (HKLM ve kullanıcı kovanları; bkz. KioskMode)
        public IKioskRegistry Kiosk { get; init; } = new MemoryKioskRegistry();
        // Eş önbelleğinin güvenlik duvarı kuralı ve sunucusunun dinlediği adres (bkz. PeerCache)
        public Func<string, Task<(int Exit, string Output)>> PeerCacheFirewall { get; init; } = Refusing.Script;
        public Func<IPEndPoint> PeerCacheEndpoint { get; init; } = Refusing.LoopbackEndpoint;
        // Güç işlemi (kapatma, yeniden başlatma, oturum kapatma, kilitleme; bkz. PowerActions). Dönen: uygulandı mı
        public Func<PowerOperation, int, bool> Power { get; init; } = Refusing.Power;
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

        // Servisin gerçek makinesi. Yalnızca Program çağırır (testler bağlamı AgentHarness ile kurar).
        internal static MachineSeams Production() => new MachineSeams
        {
            FirewallScripts = NetworkIsolation.RunPowerShellAsync,
            Kiosk = new WindowsKioskRegistry(),
            PeerCacheFirewall = PeerCache.RunPowerShellAsync,
            PeerCacheEndpoint = () => new IPEndPoint(IPAddress.IPv6Any, PeerCache.Port),
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
        };

        // Kapı verilmeyen her yolun sahteleri: makineye dokunmaz, reddeder ya da "yok" der
        internal static class Refusing
        {
            public const string ScriptOutput = "testlerde güvenlik duvarına dokunulmaz";

            public static Task<(int Exit, string Output)> Script(string script) => Task.FromResult((1, ScriptOutput));

            // Eş önbelleği yalnızca 127.0.0.1'de rastgele bir portta açılır (8817'de, bütün arabirimlerde değil)
            public static IPEndPoint LoopbackEndpoint() => new IPEndPoint(IPAddress.Loopback, 0);

            public static bool Power(PowerOperation operation, int taskId) =>
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
