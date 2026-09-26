using System;
using System.IO;
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
    [SupportedOSPlatform("windows")]
    public static class AgentCredentials
    {
        public const string SecretFileName = "agent.secret";
        public const string EnrollTokenFileName = "enroll.token";
        public const string BypassSecretFileName = "bypass.secret";

        // appsettings.json'dan (ve eski sürümlerin kullandığı sistem ortam değişkenlerinden) güvenli depoya
        // taşınan gizli ayarlar. Sistem ortam değişkenlerini ve kurulum klasöründeki dosyayı her kullanıcı
        // okuyabildiği için gizli değer orada bırakılmaz.
        private static readonly (string Key, string EnvVar, string File)[] MovableSecrets =
        {
            ("BypassSecret", "POPS_BYPASS_SECRET", BypassSecretFileName),
            ("EnrollToken", "POPS_ENROLL_TOKEN", EnrollTokenFileName),
        };

        // Sunucu secret'ı secrets.token_urlsafe(32), jetonu token_urlsafe(24) ile üretir. HTTP başlığına
        // girecek değer yalnızca bu alfabeyle kabul edilir.
        private static readonly Regex TokenRegex = new Regex(@"^[A-Za-z0-9_-]{16,256}$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static bool _badTokenLogged;

        public sealed class SecretRecord
        {
            [JsonPropertyName("secret")] public string Secret { get; set; }
            [JsonPropertyName("pc_name")] public string PcName { get; set; }
            [JsonPropertyName("saved_at")] public long SavedAt { get; set; }
        }

        public static bool IsWellFormed(string value) => value != null && TokenRegex.IsMatch(value);

        // Servis açılışında: güvenli klasörü (ACL dahil) hazırlar ve açıkta kalan gizli ayarları taşır.
        public static void Initialize()
        {
            try { SecureStore.EnsureDirectory(); }
            catch (Exception ex) { POpsHelpers.Log("SECURE", $"Güvenli klasör hazırlanamadı ({SecureStore.Dir}): {ex.Message}", true); }
            MigrateSecrets();
        }

        // ==========================================
        // CİHAZ SECRET'I
        // ==========================================
        public static string LoadSecret()
        {
            string primaryPath = SecureStore.PathOf(SecretFileName);
            string mirrorPath = MirrorPath();
            SecretRecord primary = ParseRecord(SecureStore.Read(primaryPath));
            SecretRecord mirror = mirrorPath == null ? null : ParseRecord(SecureStore.Read(mirrorPath, requireTrustedOwner: true));
            SecretRecord best = Newest(primary, mirror);
            if (best == null) return null;

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
        public static bool SaveSecret(string secret, string pcName)
        {
            var record = new SecretRecord { Secret = secret, PcName = pcName, SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            bool saved = TryWriteRecord(SecureStore.PathOf(SecretFileName), record);
            string mirrorPath = MirrorPath();
            if (mirrorPath != null) saved |= TryWriteRecord(mirrorPath, record);
            return saved;
        }

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

        private static bool TryWriteRecord(string path, SecretRecord record)
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

        // PersistDir: dondurma yazılımının geri almadığı yerel NTFS klasörü (ör. Deep Freeze ThawSpace).
        // Ayarlı değilse ya da kullanılamıyorsa null.
        private static string MirrorPath()
        {
            string dir = POpsHelpers.GetSetting("PersistDir", "POPS_PERSIST_DIR");
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
                return Path.Combine(dir, SecretFileName);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("SECURE", $"PersistDir kullanılamıyor ({dir}): {ex.Message}", true);
                return null;
            }
        }

        // ==========================================
        // ENROLL JETONU
        // ==========================================
        public static string GetEnrollToken()
        {
            string token = SecureStore.Read(SecureStore.PathOf(EnrollTokenFileName)) ?? POpsHelpers.ReadConfigValue("EnrollToken");
            if (token == null) return null;
            if (!IsWellFormed(token))
            {
                if (!_badTokenLogged) POpsHelpers.Log("SECURE", "Enroll jetonu biçimi geçersiz, gönderilmiyor.", true);
                _badTokenLogged = true;
                return null;
            }
            return token;
        }

        // Jeton tek kullanımlıktır; secret alındıktan sonra saklamanın anlamı yok.
        public static void ForgetEnrollToken()
        {
            string path = SecureStore.PathOf(EnrollTokenFileName);
            if (File.Exists(path)) SecureStore.Delete(path);
        }

        // ==========================================
        // ÇEVRİMDIŞI BYPASS GİZLİ ANAHTARI
        // ==========================================
        // Tanımlı değilse null döner ve çevrimdışı bypass devre dışı kalır.
        public static string GetBypassSecret() =>
            SecureStore.Read(SecureStore.PathOf(BypassSecretFileName)) ?? POpsHelpers.ReadConfigValue("BypassSecret");

        // ==========================================
        // GİZLİ AYARLARIN TAŞINMASI
        // ==========================================
        // appsettings.json'a (ya da eski sürümlerin okuduğu sistem ortam değişkenine) yazılmış gizli değer
        // güvenli depoya taşınır ve kaynağından silinir. Dosyaya elle yazılan yeni değer bir sonraki açılışta
        // depodakinin yerine geçer. Taşıma başarısız olursa kaynak olduğu gibi bırakılır.
        public static void MigrateSecrets()
        {
            foreach (var (_, envVar, file) in MovableSecrets)
            {
                try
                {
                    string value = Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.Machine)?.Trim();
                    if (string.IsNullOrEmpty(value)) continue;
                    if (!File.Exists(SecureStore.PathOf(file))) SecureStore.WriteProtected(SecureStore.PathOf(file), value);
                    Environment.SetEnvironmentVariable(envVar, null, EnvironmentVariableTarget.Machine);
                    POpsHelpers.Log("SECURE", $"{envVar} sistem ortam değişkeninden güvenli depoya taşındı ve silindi (ortam değişkenleri her kullanıcıya açıktır).");
                }
                catch (Exception ex) { POpsHelpers.Log("SECURE", $"{envVar} taşınamadı: {ex.Message}", true); }
            }

            foreach (string path in POpsHelpers.ConfigPaths)
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
                            SecureStore.WriteProtected(SecureStore.PathOf(file), value);
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
