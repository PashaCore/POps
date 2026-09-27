using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace POps.Shared
{
    // Ajan bileşenlerinin ortak yardımcıları: sürüm, log, ayar okuma, cihaz kimliği.
    // Eskiden her bileşende ayrı bir kopya (OmyoHelpers.cs) vardı ve kopyalar ayrışmıştı: örneğin ayarı önce
    // kurulum klasöründen okuyan düzeltme yalnızca ajandaydı; watchdog, updater ve vision yalnızca C:\POps'a
    // bakıyor, MSI kurulumunda (C:\Program Files\POps) sunucu adresini bulamayıp 127.0.0.1'e düşebiliyordu.
    [SupportedOSPlatform("windows")]
    public static class POpsHelpers
    {
        public const string MachineLogDir = @"C:\POpsLogs";
        public const string IdentityPath = @"C:\POpsData\identity.key";
        private static readonly object LogLock = new object();

        // ==========================================
        // 0. BİLEŞEN VE SÜRÜM
        // ==========================================
        // Bileşen adı her exe'nin açılışında ayarlanır. "Agent" ve "Updater" SYSTEM olarak çalışır ve
        // C:\POpsLogs\POps_<tarih>.log'a yazar. Kullanıcı oturumunda çalışanlar ("Watchdog", "Vision") oraya
        // yazamaz (klasör SYSTEM/Administrators'a kilitli), kendi %LOCALAPPDATA%\POps\Logs klasörlerine yazar.
        public static string Component { get; set; } = "Agent";

        private static readonly HashSet<string> UserSessionComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Watchdog", "Vision" };

        public static bool LogsToUserProfile => UserSessionComponents.Contains(Component);

        // Sürüm kök VERSION dosyasından gelir (Agent/Directory.Build.props -> assembly). Bütün bileşenler aynı
        // VERSION ile derlendiği için ortak kütüphanenin sürümü bileşenin sürümüdür. "+<commit>" atılır, önüne
        // "v" eklenir (ör. "v0.1.3-alpha").
        public static string AppVersion
        {
            get
            {
                var asm = typeof(POpsHelpers).Assembly;
                string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                           ?? asm.GetName().Version?.ToString()
                           ?? "0.0.0";
                int plus = v.IndexOf('+');
                if (plus >= 0) v = v.Substring(0, plus);
                return v.StartsWith("v") ? v : "v" + v;
            }
        }

        // ==========================================
        // 1. LOG
        // ==========================================
        // Testler logu geçici bir klasöre yönlendirir (C:\POpsLogs'a asla dokunmasınlar)
        internal static string LogDirectoryOverride { get; set; }

        public static string LogDirectory =>
            LogDirectoryOverride
            ?? (LogsToUserProfile
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POps", "Logs")
                : MachineLogDir);

        public static string LogFilePath(DateTime day) =>
            Path.Combine(LogDirectory, LogsToUserProfile ? $"POps{Component}_{day:yyyyMMdd}.log" : $"POps_{day:yyyyMMdd}.log");

        // C:\POpsLogs yalnızca SYSTEM ve Administrators'a açıktır (izin devralınmaz). C:\ altındaki varsayılan izinle
        // oturum açan her kullanıcı buraya dosya ya da bağlantı (hardlink/junction) bırakabiliyor, SYSTEM olarak
        // yazılan logu başka bir dosyaya yönlendirebiliyor ve logları okuyabiliyordu. Kullanıcı oturumundaki
        // bileşenler için (kendi klasörleri) bir şey yapmaz.
        public static void SecureLogDirectory()
        {
            if (LogsToUserProfile || LogDirectoryOverride != null) return;
            try
            {
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                var dir = new DirectoryInfo(MachineLogDir);
                if (dir.Exists) dir.SetAccessControl(sec);
                else dir.Create(sec);
            }
            catch (Exception ex) { Log("HELPERS", $"{MachineLogDir} izinleri ayarlanamadı: {ex.Message}", true); }
        }

        public static void Log(string component, string message, bool isError = false)
        {
            try
            {
                string dir = LogDirectory;
                if (!Directory.Exists(dir))
                {
                    if (LogsToUserProfile || LogDirectoryOverride != null) Directory.CreateDirectory(dir);
                    else SecureLogDirectory();
                }

                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string errorTag = isError ? "[ERROR]" : "[INFO ]";
                string logLine = $"[{timestamp}] {errorTag} [{component}] {message}{Environment.NewLine}";

                // Aynı süreçteki iş parçacıkları için kilit
                lock (LogLock)
                {
                    File.AppendAllText(LogFilePath(DateTime.Now), logLine);
                }

                // Konsol ekranı açıksa oraya da renkli yaz (Debug için)
                if (Environment.UserInteractive)
                {
                    Console.ForegroundColor = isError ? ConsoleColor.Red : ConsoleColor.Yellow;
                    Console.Write($"[{timestamp}] [{component}] ");
                    Console.ResetColor();
                    Console.WriteLine(message);
                }
            }
            catch
            {
                // Log yazarken hata olursa sistemi çökertme, yut.
            }
        }

        // ==========================================
        // 2. AYARLAR
        // ==========================================
        // Ayar dosyası önce bileşenin kurulu olduğu klasörde (MSI: C:\Program Files\POps), sonra eski sabit konumda
        // (C:\POps) aranır; her ayar için ilk dolu değer kullanılır. Testler yolları değiştirebilir.
        public static IReadOnlyList<string> ConfigPaths { get; internal set; } = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            @"C:\POps\appsettings.json",
        };

        public static string GetServerUrl()
        {
            string defaultUrl = "http://127.0.0.1:8000"; // Son çare (Fallback)

            // Sunucu adresi koda gömülmez: önce POPS_SERVER_URL ortam değişkeni, sonra appsettings.json
            string url = GetSetting("ServerUrl", "POPS_SERVER_URL");
            if (url != null) return url.TrimEnd('/');

            Log("HELPERS", $"ServerUrl tanımlı değil ({string.Join(" | ", ConfigPaths)}); {defaultUrl} kullanılıyor.", true);
            return defaultUrl;
        }

        // Cihaz secret'ı, enroll jetonu ve sunucunun gönderdiği komutlar (execute, set_secret, set_identity)
        // yalnızca şifreli kanaldan (https/wss) taşınır: düz ws:// üzerinde aynı ağdaki biri bunları okuyup
        // SYSTEM olarak komut gönderebilirdi. Düz http yalnızca aynı makinedeki (loopback) sunucu için kabul edilir.
        public static bool IsSecureServerUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

        // Gizli olmayan bir ayar: önce sistem ortam değişkeni, sonra appsettings.json.
        public static string GetSetting(string key, string envVar)
        {
            string env = Environment.GetEnvironmentVariable(envVar);
            return !string.IsNullOrWhiteSpace(env) ? env.Trim() : ReadConfigValue(key);
        }

        // Bir ayarı sırayla ConfigPaths içindeki dosyalarda arar; hiçbirinde dolu değilse null döner.
        public static string ReadConfigValue(string key) => ReadConfigValue(key, ConfigPaths);

        public static string ReadConfigValue(string key, IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty(key, out JsonElement element) && element.ValueKind == JsonValueKind.String)
                    {
                        string value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
                }
                catch (Exception ex)
                {
                    Log("HELPERS", $"Config okuma hatası ({path}): {ex.Message}", true);
                }
            }
            return null;
        }

        // ==========================================
        // 3. CİHAZ KİMLİĞİ (HW_ID)
        // ==========================================
        public static string GetHardwareId()
        {
            try
            {
                if (File.Exists(IdentityPath))
                {
                    string savedId = File.ReadAllText(IdentityPath).Trim();
                    if (!string.IsNullOrEmpty(savedId) && savedId.StartsWith("HW-")) return savedId;
                }
            }
            catch { }

            return "HW-UNKNOWN";
        }
    }
}
