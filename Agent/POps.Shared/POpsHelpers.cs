#nullable disable
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;

namespace POps.Shared
{
    // Ajan bileşenlerinin ortak yardımcıları: sürüm, log, ayar okuma.
    // Eskiden her bileşende ayrı bir kopya vardı ve kopyalar ayrışmıştı: örneğin ayarı önce
    // kurulum klasöründen okuyan düzeltme yalnızca ajandaydı; watchdog, updater ve vision yalnızca C:\POps'a
    // bakıyor, MSI kurulumunda (C:\Program Files\POps) sunucu adresini bulamayıp 127.0.0.1'e düşebiliyordu.
    [SupportedOSPlatform("windows")]
    public static class POpsHelpers
    {
        // SYSTEM olarak çalışanların log klasörü: appsettings.json "LogDirectory" (servis açılışta seçer, updater komut
        // satırından alır; bkz. FolderSettings), verilmezse C:\POpsLogs
        public static string MachineLogDir { get; set; } = FolderSettings.DefaultLogDirectory;
        private static readonly object LogLock = new object();
        // Log klasörü kilitlenirken yazılan hata logu klasörü yeniden kilitlemeye çalışmasın (sonsuz özyineleme)
        [ThreadStatic] private static bool _securingLogDirectory;

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

        // C:\POpsLogs (ya da LogDirectory) yalnızca SYSTEM ve Administrators'a açıktır (izin devralınmaz). C:\ altındaki
        // varsayılan izinle oturum açan her kullanıcı buraya dosya ya da bağlantı (hardlink/junction) bırakabiliyor, SYSTEM
        // olarak yazılan logu başka bir dosyaya yönlendirebiliyor ve logları okuyabiliyordu. Sahibi güvenilir değilse
        // (klasörü bir kullanıcı önceden açtıysa) sahibi SYSTEM yapılır. Kullanıcı oturumundaki bileşenler için (kendi
        // klasörleri) bir şey yapmaz.
        public static void SecureLogDirectory()
        {
            if (LogsToUserProfile || LogDirectoryOverride != null || _securingLogDirectory) return;
            _securingLogDirectory = true;
            try
            {
                string dir = MachineLogDir;
                string error = FolderSettings.Secure(dir, usersRead: false, out bool tightened);
                if (error != null) Log("HELPERS", $"{dir} izinleri ayarlanamadı: {error}", true);
                else if (tightened) Log("HELPERS", FolderSettings.TightenedNote(dir, usersRead: false));
            }
            finally { _securingLogDirectory = false; }
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

        // Sunucu adresi okunamazsa son çare (yalnızca aynı makinedeki sunucu için anlamlı)
        public const string FallbackServerUrl = "http://127.0.0.1:8000";

        public static string GetServerUrl() => ResolveServerUrl().Url;

        // Sunucu adresi ve yapılandırmanın sorunu (null: sorun yok). Adres koda gömülmez: önce POPS_SERVER_URL ortam
        // değişkeni, sonra ConfigPaths'teki ilk dolu ServerUrl. Okunamayan (bozuk JSON, erişim) bir dosya, adres başka
        // bir dosyadan gelse de sorun sayılır. Adres bulunamaz ya da geçerli bir http(s) adresi değilse son çare kullanılır.
        // Eskiden bu durum yalnızca loga yazılıyordu; ajan sağlıklı görünüp hiçbir sunucuya bağlanmıyordu.
        public static (string Url, string Problem) ResolveServerUrl()
        {
            var problems = new List<string>();
            string url = null, source = null;
            string env = Environment.GetEnvironmentVariable("POPS_SERVER_URL");
            if (!string.IsNullOrWhiteSpace(env))
            {
                url = env.Trim();
                source = "POPS_SERVER_URL";
            }
            bool anyFile = false;
            foreach (string path in ConfigPaths)
            {
                if (!File.Exists(path)) continue;
                anyFile = true;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (url == null && doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("ServerUrl", out JsonElement element) && element.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(element.GetString()))
                    {
                        url = element.GetString().Trim();
                        source = path;
                    }
                }
                catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    problems.Add($"{path} okunamadı: {ex.Message}");
                }
            }

            if (url != null && !(Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            {
                problems.Add($"ServerUrl geçerli bir http(s) adresi değil ({source}: {LogText.Safe(url, 100)})");
                url = null;
            }
            if (url == null && problems.Count == 0)
                problems.Add(anyFile
                    ? $"ServerUrl tanımlı değil ({string.Join(" | ", ConfigPaths)})"
                    : $"appsettings.json bulunamadı ({string.Join(" | ", ConfigPaths)})");

            string problem = problems.Count > 0 ? string.Join("; ", problems) : null;
            if (url == null)
            {
                Log("HELPERS", $"[HATA] Yapılandırma okunamadı: {problem}. Son çare {FallbackServerUrl} kullanılıyor.", true);
                return (FallbackServerUrl, problem);
            }
            return (url.TrimEnd('/'), problem);
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

        // Klasör ayarı (LogDirectory, DataDirectory): ConfigPaths'teki ilk dolu metin değeri. Log yazmaz (log klasörü henüz
        // seçilmedi); okunamayan dosya atlanır (ResolveServerUrl onu sorun olarak bildirir). Metin olmayan değer problem'e yazılır.
        public static string ReadConfigText(string key, out string problem)
        {
            problem = null;
            foreach (string path in ConfigPaths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(key, out JsonElement element)
                        || element.ValueKind == JsonValueKind.Null) continue;
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        problem = $"{key} bir metin (klasör yolu) değil ({path})";
                        return null;
                    }
                    string value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return null;
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
    }
}
