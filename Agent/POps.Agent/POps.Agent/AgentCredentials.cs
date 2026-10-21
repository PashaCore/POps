using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

#nullable disable

namespace POpsAgent
{
    // Ajanın sunucuya kimliğini kanıtladığı bilgiler (Faz 3) ve diğer gizli ayarlar.
    //  * Enroll jetonu: kurulumda (MSI ENROLL_TOKEN) verilen tek kullanımlık kayıt jetonu. Cihaz secret'ı
    //    olmadığı sürece X-Enroll-Token başlığıyla gönderilir; sunucu kabul ederse cihaza özel secret'ı
    //    "set_secret" ile yollar ve jeton tüketilir.
    //  * Cihaz secret'ı: sonraki bağlantılarda X-Agent-Secret başlığıyla gönderilir.
    // Hepsi SecureStore'da (yalnızca SYSTEM/Administrators) tutulur. Dondurma yazılımı (Deep Freeze vb.)
    // C:'yi her açılışta geri aldığında yeni secret kaybolmasın diye PersistDir ayarlıysa secret dondurulmayan
    // bu klasöre de yazılır; açılışta ikisinden en yeni kayıt kullanılır.
    // Bu çalışmanın durumu (bilinen secret, bir kez loglanan uyarı) bağlamdaki CredentialStore örneğindedir (bkz.
    // AgentContext.Credentials); burada dosya adları, saf yardımcılar ve süreç varsayılanı kalır.
    [SupportedOSPlatform("windows")]
    public static class AgentCredentials
    {
        public const string SecretFileName = "agent.secret";
        public const string EnrollTokenFileName = "enroll.token";
        public const string BypassSecretFileName = "bypass.secret";
        public const string DeviceBypassSecretFileName = "bypass.device";

        // Sunucu secret'ı secrets.token_urlsafe(32), jetonu token_urlsafe(24) ile üretir. HTTP başlığına
        // girecek değer yalnızca bu alfabeyle kabul edilir.
        private static readonly Regex TokenRegex = new Regex(@"^[A-Za-z0-9_-]{16,256}$", RegexOptions.Compiled);

        // Süreç varsayılanı: servis açılışta bağlamın örneğine çevirir (bkz. AgentContext.UseForProcess). Worker ve kurduğu
        // servisler bağlamın örneğini kullanır; bunu Worker bölmesinin b3/b4 adımlarına kadar statik kalan HTTP kodu
        // (FileTransfer, DnsPolicyMonitor: AddHttpAuth) ve aşağıdaki biçimleri çağıran SharedState testleri okur. Başlangıç
        // değeri yolları her kullanımda SecureStore.Dir ve POpsHelpers.ConfigPaths'ten alır.
        public static CredentialStore Process { get; internal set; } = new CredentialStore(() => SecureStore.Dir, () => POpsHelpers.ConfigPaths);

        public static string CurrentSecret => Process.CurrentSecret;
        public static void AddHttpAuth(HttpRequestMessage request, string hwId) => Process.AddHttpAuth(request, hwId);
        public static string LoadSecret() => Process.LoadSecret();
        public static bool SaveSecret(string secret, string pcName) => Process.SaveSecret(secret, pcName);
        public static string GetEnrollToken() => Process.GetEnrollToken();
        public static string GetBypassSecret() => Process.GetBypassSecret();
        public static string GetDeviceBypassSecret(out bool present) => Process.GetDeviceBypassSecret(out present);
        public static bool SaveDeviceBypassSecret(string secret) => Process.SaveDeviceBypassSecret(secret);
        public static void MigrateSecrets() => Process.MigrateSecrets();

        public sealed class SecretRecord
        {
            [JsonPropertyName("secret")] public string Secret { get; set; }
            [JsonPropertyName("pc_name")] public string PcName { get; set; }
            [JsonPropertyName("saved_at")] public long SavedAt { get; set; }
        }

        public static bool IsWellFormed(string value) => value != null && TokenRegex.IsMatch(value);

        public static SecretRecord ParseRecord(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var record = JsonSerializer.Deserialize<SecretRecord>(json);
                return record != null && IsWellFormed(record.Secret) ? record : null;
            }
            catch (JsonException) { return null; }
        }

        // Eşitlikte birincil depo tercih edilir.
        public static SecretRecord Newest(SecretRecord primary, SecretRecord mirror)
        {
            if (primary == null) return mirror;
            if (mirror == null) return primary;
            return mirror.SavedAt > primary.SavedAt ? mirror : primary;
        }

        // PersistDir: dondurma yazılımının geri almadığı yerel NTFS klasörü (ör. Deep Freeze ThawSpace). Secret ve
        // donanım bağı (hw.bind) orada da tutulur. Ayarlı değilse ya da kullanılamıyorsa null. Ayar POpsHelpers.ConfigPaths'ten
        // (HardwareBinding ve Generalizer; Worker bölmesinin b3 adımına kadar).
        internal static string PersistPath(string fileName) => PersistPath(fileName, POpsHelpers.ConfigPaths);

        // configPaths: ayarın arandığı dosyalar (CredentialStore: bağlamın AgentPaths.ConfigPaths'i)
        internal static string PersistPath(string fileName, IReadOnlyList<string> configPaths)
        {
            string dir = POpsHelpers.GetSetting("PersistDir", "POPS_PERSIST_DIR", configPaths);
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                if (!Path.IsPathFullyQualified(dir))
                {
                    POpsHelpers.Log("SECURE", $"PersistDir tam yol değil, yok sayıldı: {dir}", true);
                    return null;
                }
                // FAT/exFAT sürücüde ACL yoktur; secret orada herkesçe okunabilirdi
                var drive = new DriveInfo(Path.GetPathRoot(dir));
                if (!drive.IsReady || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    POpsHelpers.Log("SECURE", $"PersistDir yerel bir NTFS sürücüde değil, yok sayıldı: {dir}", true);
                    return null;
                }
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, fileName);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("SECURE", $"PersistDir kullanılamıyor ({dir}): {ex.Message}", true);
                return null;
            }
        }
    }

    // Bir ajanın kimlik bilgileri (bkz. AgentCredentials): bu çalışmada bilinen cihaz secret'ı, gizli dosyalar (güvenli depo)
    // ve appsettings.json'daki gizli ayarların taşınması. Servis için Program bir kez kurar (AgentPaths), testler kendi geçici
    // klasörleriyle kurar.
    [SupportedOSPlatform("windows")]
    public sealed class CredentialStore
    {
        // appsettings.json'dan (ve eski sürümlerin kullandığı sistem ortam değişkenlerinden) güvenli depoya
        // taşınan gizli ayarlar. Sistem ortam değişkenlerini ve kurulum klasöründeki dosyayı her kullanıcı
        // okuyabildiği için gizli değer orada bırakılmaz.
        private static readonly (string Key, string EnvVar, string File)[] MovableSecrets =
        {
            ("BypassSecret", "POPS_BYPASS_SECRET", AgentCredentials.BypassSecretFileName),
            ("EnrollToken", "POPS_ENROLL_TOKEN", AgentCredentials.EnrollTokenFileName),
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // Güvenli depo ve ayar dosyaları (servis: AgentPaths.SecureDir ve ConfigPaths)
        private readonly Func<string> _secureDir;
        private readonly Func<IReadOnlyList<string>> _configPaths;

        private bool _badTokenLogged;

        // paths: güvenli depo (SecureDir) ve gizli ayarların arandığı dosyalar (ConfigPaths)
        public CredentialStore(AgentPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _secureDir = () => paths.SecureDir;
            _configPaths = () => paths.ConfigPaths;
        }

        // Yollar her kullanımda okunur: süreç varsayılanı içindir (bkz. AgentCredentials.Process)
        internal CredentialStore(Func<string> secureDir, Func<IReadOnlyList<string>> configPaths)
        {
            _secureDir = secureDir ?? throw new ArgumentNullException(nameof(secureDir));
            _configPaths = configPaths ?? throw new ArgumentNullException(nameof(configPaths));
        }

        private string SecureFile(string name) => Path.Combine(_secureDir(), name);

        // Bu çalışmada bilinen cihaz secret'ı (LoadSecret / SaveSecret günceller). Komut ve Vision WebSocket'i ile
        // ajan HTTP çağrıları buradan okur; diske yazılamasa bile servis yeniden başlayana kadar kullanılır.
        private volatile string _currentSecret;
        public string CurrentSecret => _currentSecret;

        // Ajan HTTP uçları (inventory, policy_alert) sunucuda enforce_agent_auth açıkken X-Agent-Id + X-Agent-Secret
        // bekler. Secret yalnızca şifreli (ya da aynı makinedeki) sunucuya gönderilir.
        public void AddHttpAuth(HttpRequestMessage request, string hwId)
        {
            string secret = _currentSecret;
            if (secret == null || string.IsNullOrEmpty(hwId) || !POpsHelpers.IsSecureServerUrl(request.RequestUri?.ToString())) return;
            request.Headers.TryAddWithoutValidation("X-Agent-Id", hwId);
            request.Headers.TryAddWithoutValidation("X-Agent-Secret", secret);
        }

        // Servis açılışında: güvenli klasörü (ACL dahil) hazırlar ve açıkta kalan gizli ayarları taşır.
        public void Initialize()
        {
            string secureDir = _secureDir();
            try { SecureStore.EnsureDirectory(secureDir); }
            catch (Exception ex) { POpsHelpers.Log("SECURE", $"Güvenli klasör hazırlanamadı ({secureDir}): {ex.Message}", true); }
            MigrateSecrets();
        }

        // ==========================================
        // CİHAZ SECRET'I
        // ==========================================
        public string LoadSecret()
        {
            string primaryPath = SecureFile(AgentCredentials.SecretFileName);
            string mirrorPath = MirrorPath();
            AgentCredentials.SecretRecord primary = AgentCredentials.ParseRecord(SecureStore.Read(primaryPath));
            AgentCredentials.SecretRecord mirror = mirrorPath == null ? null : AgentCredentials.ParseRecord(SecureStore.Read(mirrorPath, requireTrustedOwner: true));
            AgentCredentials.SecretRecord best = AgentCredentials.Newest(primary, mirror);
            if (best == null) return null;
            _currentSecret = best.Secret;

            if (best != primary)
            {
                // C: dondurma yazılımıyla geri alınmış: dondurulmayan klasördeki daha yeni secret geri yazılır
                TryWriteRecord(primaryPath, best);
                POpsHelpers.Log("SECURE", "Cihaz secret'ı dondurulmayan klasörden (PersistDir) geri yüklendi.");
            }
            if (mirrorPath != null && best != mirror) TryWriteRecord(mirrorPath, best);
            return best.Secret;
        }

        // En az bir konuma yazılabildiyse true.
        public bool SaveSecret(string secret, string pcName)
        {
            _currentSecret = secret;
            var record = new AgentCredentials.SecretRecord { Secret = secret, PcName = pcName, SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            bool saved = TryWriteRecord(SecureFile(AgentCredentials.SecretFileName), record);
            string mirrorPath = MirrorPath();
            if (mirrorPath != null) saved |= TryWriteRecord(mirrorPath, record);
            return saved;
        }

        private static bool TryWriteRecord(string path, AgentCredentials.SecretRecord record)
        {
            try
            {
                SecureStore.WriteProtected(path, JsonSerializer.Serialize(record, WriteOptions));
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("SECURE", $"Cihaz secret'ı yazılamadı ({path}): {ex.Message}", true);
                return false;
            }
        }

        private string MirrorPath() => AgentCredentials.PersistPath(AgentCredentials.SecretFileName, _configPaths());

        // ==========================================
        // ENROLL JETONU
        // ==========================================
        public string GetEnrollToken()
        {
            string token = SecureStore.Read(SecureFile(AgentCredentials.EnrollTokenFileName)) ?? POpsHelpers.ReadConfigValue("EnrollToken", _configPaths());
            if (token == null) return null;
            if (!AgentCredentials.IsWellFormed(token))
            {
                if (!_badTokenLogged) POpsHelpers.Log("SECURE", "Enroll jetonu biçimi geçersiz, gönderilmiyor.", true);
                _badTokenLogged = true;
                return null;
            }
            return token;
        }

        // Jeton tek kullanımlıktır; secret alındıktan sonra saklamanın anlamı yok.
        public void ForgetEnrollToken()
        {
            string path = SecureFile(AgentCredentials.EnrollTokenFileName);
            if (File.Exists(path)) SecureStore.Delete(path);
        }

        // ==========================================
        // ÇEVRİMDIŞI BYPASS GİZLİ ANAHTARI
        // ==========================================
        // Tanımlı değilse null döner ve çevrimdışı bypass devre dışı kalır.
        public string GetBypassSecret() =>
            SecureStore.Read(SecureFile(AgentCredentials.BypassSecretFileName)) ?? POpsHelpers.ReadConfigValue("BypassSecret", _configPaths());

        // Dosya varsa okuma/biçim hatasında eski filo anahtarına geri düşülmez; çağıran present değerini korur.
        public string GetDeviceBypassSecret(out bool present)
        {
            string path = SecureFile(AgentCredentials.DeviceBypassSecretFileName);
            present = File.Exists(path);
            return present ? SecureStore.Read(path) : null;
        }

        public bool SaveDeviceBypassSecret(string secret)
        {
            if (!DeviceBypassSecret.TryDecode(secret, out _)) return false;
            try
            {
                SecureStore.WriteProtected(SecureFile(AgentCredentials.DeviceBypassSecretFileName), secret.Trim());
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("SECURE", $"Bypass cihaz anahtarı yazılamadı ({AgentCredentials.DeviceBypassSecretFileName}): {ex.Message}", true);
                return false;
            }
        }

        // ==========================================
        // GİZLİ AYARLARIN TAŞINMASI
        // ==========================================
        // appsettings.json'a (ya da eski sürümlerin okuduğu sistem ortam değişkenine) yazılmış gizli değer
        // güvenli depoya taşınır ve kaynağından silinir. Dosyaya elle yazılan yeni değer bir sonraki açılışta
        // depodakinin yerine geçer. Taşıma başarısız olursa kaynak olduğu gibi bırakılır.
        public void MigrateSecrets()
        {
            foreach (var (_, envVar, file) in MovableSecrets)
            {
                try
                {
                    string value = Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.Machine)?.Trim();
                    if (string.IsNullOrEmpty(value)) continue;
                    if (!File.Exists(SecureFile(file))) SecureStore.WriteProtected(SecureFile(file), value);
                    Environment.SetEnvironmentVariable(envVar, null, EnvironmentVariableTarget.Machine);
                    POpsHelpers.Log("SECURE", $"{envVar} sistem ortam değişkeninden güvenli depoya taşındı ve silindi (ortam değişkenleri her kullanıcıya açıktır).");
                }
                catch (Exception ex) { POpsHelpers.Log("SECURE", $"{envVar} taşınamadı: {ex.Message}", true); }
            }

            foreach (string path in _configPaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) continue;

                    bool changed = false;
                    foreach (var (key, _, file) in MovableSecrets)
                    {
                        if (!root.TryGetPropertyValue(key, out JsonNode node)) continue;
                        string value = node is JsonValue v && v.TryGetValue(out string s) ? s.Trim() : null;
                        if (!string.IsNullOrEmpty(value))
                        {
                            SecureStore.WriteProtected(SecureFile(file), value);
                            POpsHelpers.Log("SECURE", $"{key} {path} dosyasından güvenli depoya taşındı.");
                        }
                        root.Remove(key);
                        changed = true;
                    }
                    if (changed) SecureStore.WriteProtected(path, root.ToJsonString(WriteOptions));
                }
                catch (Exception ex) { POpsHelpers.Log("SECURE", $"{path} içindeki gizli ayarlar taşınamadı: {ex.Message}", true); }
            }
        }
    }
}
