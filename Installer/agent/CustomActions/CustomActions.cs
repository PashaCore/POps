using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using WixToolset.Dtf.WindowsInstaller;

// Birim testleri (Agent/POps.Tests) iç mantığa (Setup, Layout) erişir; proje GenerateAssemblyInfo=false olduğu için burada
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("POps.Tests")]

namespace POps.Installer
{
    // POps Agent MSI'ının ertelenmiş (deferred, SYSTEM) custom action'ları.
    //
    // appsettings.json ve gizli değerler MSI'ın kendi dosyaları değildir: güncelleme (major upgrade)
    // eski ürünü kaldırırken onları silmesin, gizli değerler MSI tablolarına, komut satırına ya da loga
    // girmesin diye burada yazılır. Değerler loglanmaz; yalnızca hangi anahtarın yazıldığı loglanır.
    // Ajan tarafındaki karşılığı: Agent/POps.Agent/POps.Agent/{SecureStore,AgentCredentials}.cs
    public static class CustomActions
    {
        [CustomAction]
        public static ActionResult Configure(Session session)
        {
            string error = Setup.Configure(ToDictionary(session.CustomActionData), Layout.Default, session.Log);
            if (error == null) return ActionResult.Success;

            session.Log("POps: HATA: " + error);
            try
            {
                using (var record = new Record(1) { FormatString = "[1]" })
                {
                    record[1] = error;
                    session.Message(InstallMessage.Error, record);
                }
            }
            catch { }
            return ActionResult.Failure;
        }

        // Hemen (immediate), CostFinalize'dan sonra, dosyalar kopyalanmadan ÖNCE: INSTALLFOLDER'ı doğrular ve kurulumdan
        // önceki durumunu (yeni / yalnızca POps dosyaları / başka dosyalar) POPS_INSTALLDIR_STATE ile Configure'a aktarır
        [CustomAction]
        public static ActionResult CheckInstallFolder(Session session)
        {
            string dir = Setup.Clean(session["INSTALLFOLDER"]);
            string error = Setup.CheckInstallFolder(dir, Setup.DefaultProtectedFolders(), KnownFileNames(session), out string state);
            if (error != null) return Fail(session, error);
            session["POPS_INSTALLDIR_STATE"] = state;
            session.Log($"POps: kurulum klasörü {dir} ({state}).");
            return ActionResult.Success;
        }

        // Paketin kurduğu dosya adları (File tablosu; "KISA|uzun" biçiminde uzun ad)
        private static ISet<string> KnownFileNames(Session session)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string value in session.Database.ExecuteStringQuery("SELECT `FileName` FROM `File`"))
                {
                    int bar = value.IndexOf('|');
                    names.Add(bar >= 0 ? value.Substring(bar + 1) : value);
                }
            }
            catch (Exception ex) { session.Log("POps: dosya listesi okunamadı: " + ex.Message); }
            return names;
        }

        private static ActionResult Fail(Session session, string error)
        {
            session.Log("POps: HATA: " + error);
            try
            {
                using (var record = new Record(1) { FormatString = "[1]" })
                {
                    record[1] = error;
                    session.Message(InstallMessage.Error, record);
                }
            }
            catch { }
            return ActionResult.Failure;
        }

        [CustomAction]
        public static ActionResult CleanupLegacy(Session session)
        {
            Setup.CleanupLegacy(Value(session.CustomActionData, "INSTALLFOLDER"), Layout.Default, session.Log);
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult RemoveConfig(Session session)
        {
            Setup.RemoveConfig(Value(session.CustomActionData, "INSTALLFOLDER"), session.Log);
            // Karantinadayken kaldırılsa bile Görev Yöneticisi vb. kapalı kalmaz
            Setup.RestoreKiosk(Layout.Default, new POps.Shared.WindowsKioskRegistry(), session.Log);
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult KeepPackage(Session session)
        {
            Setup.KeepPackage(Value(session.CustomActionData, "ORIGINAL_MSI"), Layout.Default, session.Log);
            return ActionResult.Success;
        }

        private static Dictionary<string, string> ToDictionary(CustomActionData data)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (data != null) foreach (string key in data.Keys) result[key] = data[key];
            return result;
        }

        private static string Value(CustomActionData data, string key) =>
            data != null && data.ContainsKey(key) ? Setup.Clean(data[key]) : null;
    }

    // Kurulumun dokunduğu sabit konumlar (testte geçici klasörlerle değiştirilir)
    internal sealed class Layout
    {
        public string DataDir = @"C:\POpsData";
        public string SecureDir = @"C:\POpsData\secure";
        public string LogDir = @"C:\POpsLogs";
        public IList<string> LegacyDirs;

        public static Layout Default => new Layout { LegacyDirs = DefaultLegacyDirs() };

        // Eski sürümlerin (MSI'sız) kurulduğu yerler. Sıra, ayar taşımadaki önceliktir: yayımlanmış eski
        // sürümler (0.1.2-alpha dahil) ayarı yalnızca C:\POps'tan okuduğu için canlı değer oradadır.
        private static IList<string> DefaultLegacyDirs()
        {
            string programFiles64 = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return new[]
            {
                @"C:\POps",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "POps"),
                Path.Combine(programFiles64, "POps"),
            };
        }
    }

    internal static class Setup
    {
        private const string ConfigName = "appsettings.json";
        private const string EnrollTokenFile = "enroll.token";
        private const string BypassSecretFile = "bypass.secret";
        private const string CapabilitiesFile = "capabilities.json";

        // Sunucu jetonu secrets.token_urlsafe(24) ile üretir; ajan da aynı alfabeyi bekler
        private static readonly Regex TokenRegex = new Regex(@"^[A-Za-z0-9_-]{16,256}$");

        // Kurulum klasöründe bulunmaması gereken eski dosyalar (eski ad, eski güncelleme betiği, geliştirme ayarları)
        private static readonly string[] JunkPatterns = { "PashaCoreAgent.*", "apply_update.bat", "appsettings.Development.json", "*.pdb" };

        // Yalnızca ayar içeren eski klasörlerden (C:\POps) silinecek dosyalar
        private static readonly string[] LegacyConfigPatterns = { "appsettings*.json", "apply_update.bat" };

        public static string Clean(string value)
        {
            value = value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        // ==========================================================================================
        // Configure: dosyalar kopyalandıktan sonra, servis başlamadan önce. Hata metni döner (null = başarılı).
        // ==========================================================================================
        public static string Configure(IDictionary<string, string> data, Layout layout, Action<string> log)
        {
            try
            {
                string Prop(string key) => data.TryGetValue(key, out string v) ? Clean(v) : null;

                string installDir = Prop("INSTALLFOLDER");
                if (installDir == null) return "INSTALLFOLDER gelmedi.";
                string configPath = Path.Combine(installDir, ConfigName);

                // Öncelik: bu kurulumun ayar dosyası, sonra eski kurulumlarınki
                var sources = new List<string> { configPath };
                sources.AddRange(layout.LegacyDirs.Where(d => !SamePath(d, installDir)).Select(d => Path.Combine(d, ConfigName)));
                List<Dictionary<string, object>> parsed = sources.Select(p => ReadJsonObject(p, log)).ToList();
                string Existing(string key) => parsed.Select(j => StringValue(j, key)).FirstOrDefault(v => v != null);

                string serverUrl = Prop("SERVER_URL") ?? Existing("ServerUrl");
                if (serverUrl == null)
                    return "SERVER_URL verilmedi ve mevcut bir kurulumda da sunucu adresi bulunamadı. Örnek: msiexec /i POps-Agent.msi SERVER_URL=https://pops.example.com";
                if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return "SERVER_URL http:// ya da https:// ile başlayan tam bir adres olmalı.";
                // Düz http'de cihaz secret'ı, enroll jetonu ve sunucu komutları ağda okunup değiştirilebilir. Yalnızca
                // aynı makinedeki test sunucusu (loopback) kabul edilir; ajan da aynı kuralla bağlanmayı reddeder.
                // Eski kurulumdan taşınan adres de bu kurala tabidir.
                if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
                    return $"SERVER_URL şifresiz http ({serverUrl}); https:// bir adres gerekli, düz http'de cihaz secret'ı ve sunucu komutları ağda açık gider. Kurulumu SERVER_URL=https://... ile yeniden başlatın.";

                string enrollToken = Prop("ENROLL_TOKEN");
                if (enrollToken != null && !TokenRegex.IsMatch(enrollToken))
                    return "ENROLL_TOKEN biçimi geçersiz; panelde üretilen jetonu olduğu gibi verin.";

                string persistDir = Prop("PERSIST_DIR") ?? Existing("PersistDir");
                if (persistDir != null && !Path.IsPathRooted(persistDir))
                    return "PERSIST_DIR tam bir klasör yolu olmalı (ör. T:\\POps).";

                if (!TryParseFlag(Prop("TERMINAL_ENABLED"), out bool? terminal))
                    return "TERMINAL_ENABLED 1 (açık) ya da 0 (kapalı) olmalı.";
                if (!TryParseFlag(Prop("VISION_ENABLED"), out bool? vision))
                    return "VISION_ENABLED 1 (açık) ya da 0 (kapalı) olmalı.";
                // Bozuk sertifika hiçbir şey yazılmadan reddedilir
                string caError = ReadServerCa(Prop("SERVER_CA_CERT"), out string caPem, out bool removeCa, out string caSubject);
                if (caError != null) return caError;

                EnsureDataDirectories(layout);
                string folderError = ApplyInstallFolderPolicy(installDir, Prop("INSTALLDIR_STATE"), log);
                if (folderError != null) return folderError;
                WriteCapabilities(layout, terminal, vision, log);
                WriteServerCa(layout, caPem, removeCa, caSubject, log);
                WriteSecret(Path.Combine(layout.SecureDir, BypassSecretFile), Prop("BYPASS_SECRET"), Existing("BypassSecret"), "BypassSecret", log);
                WriteSecret(Path.Combine(layout.SecureDir, EnrollTokenFile), enrollToken, Existing("EnrollToken"), "EnrollToken", log);

                // Mevcut dosyadaki diğer ayarlar (Logging vb.) korunur; gizli değerler dosyada kalmaz
                Dictionary<string, object> config = parsed[0] ?? new Dictionary<string, object>();
                config["ServerUrl"] = serverUrl.TrimEnd('/');
                if (persistDir != null) config["PersistDir"] = persistDir;
                config.Remove("BypassSecret");
                config.Remove("EnrollToken");

                Directory.CreateDirectory(installDir);
                WriteProtected(configPath, ToJson(config, 0) + "\r\n");
                log($"POps: {configPath} yazıldı (ServerUrl{(persistDir != null ? ", PersistDir" : "")}); yalnızca SYSTEM/Administrators erişebilir.");
                return null;
            }
            catch (Exception ex)
            {
                return "POps ayarları yazılamadı: " + ex.Message;
            }
        }

        // ==========================================================================================
        // CleanupLegacy: kurulumun sonunda, yeni servis başladıktan sonra. Hata kurulumu bozmaz.
        // ==========================================================================================
        public static void CleanupLegacy(string installDir, Layout layout, Action<string> log)
        {
            try
            {
                foreach (string dir in layout.LegacyDirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (installDir != null && SamePath(dir, installDir)) continue;
                    if (!Directory.Exists(dir)) continue;

                    // Ajan ikilileri olan klasör bütünüyle eski bir POps kurulumudur; yalnızca ayar içeren
                    // klasörden (C:\POps) sadece bilinen ayar dosyaları silinir
                    bool isInstall = File.Exists(Path.Combine(dir, "POpsAgent.exe")) || File.Exists(Path.Combine(dir, "PashaCoreAgent.exe"));
                    if (isInstall)
                    {
                        DeleteTree(dir, log);
                    }
                    else
                    {
                        foreach (string pattern in LegacyConfigPatterns)
                            foreach (string file in Directory.GetFiles(dir, pattern)) DeleteFile(file, log);
                        // Başka dosya kaldıysa klasör olduğu gibi bırakılır
                        if (!Directory.EnumerateFileSystemEntries(dir).Any()) TryDeleteDirectory(dir, log);
                    }
                    log($"POps: eski kurulum temizlendi: {dir}");
                }

                if (installDir != null && Directory.Exists(installDir))
                    foreach (string pattern in JunkPatterns)
                        foreach (string file in Directory.GetFiles(installDir, pattern)) DeleteFile(file, log);
            }
            catch (Exception ex)
            {
                log("POps: eski kurulum temizliği yarım kaldı (kurulum etkilenmedi): " + ex.Message);
            }
        }

        // ==========================================================================================
        // RemoveConfig: yalnızca gerçek kaldırmada (güncellemede değil). C:\POpsData (kimlik, cihaz
        // secret'ı) bilerek korunur: yeniden kurulan cihaz aynı kimlikle döner.
        // ==========================================================================================
        public static void RemoveConfig(string installDir, Action<string> log)
        {
            if (installDir == null) return;
            foreach (string name in new[] { ConfigName, ConfigName + ".tmp" })
            {
                string path = Path.Combine(installDir, name);
                if (File.Exists(path)) DeleteFile(path, log);
            }
        }

        // ==========================================================================================
        // KeepPackage: kurulan MSI, POpsUpdater'ın geri dönüş kaynağı olarak saklanır. Hata kurulumu bozmaz.
        // ==========================================================================================
        public static void KeepPackage(string originalMsi, Layout layout, Action<string> log)
        {
            try
            {
                if (originalMsi == null || !File.Exists(originalMsi))
                {
                    log("POps: kurulum paketi bulunamadı, saklanmadı.");
                    return;
                }
                // Windows Installer önbelleğindeki kopyada gömülü dosyalar yoktur; ondan yeniden kurulamaz
                string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer") + "\\";
                if (Path.GetFullPath(originalMsi).StartsWith(cache, StringComparison.OrdinalIgnoreCase)) return;

                string dir = Path.Combine(layout.DataDir, "packages");
                string target = Path.Combine(dir, "installed.msi");
                if (SamePath(originalMsi, target)) return;

                Directory.CreateDirectory(dir);
                string tmp = target + ".tmp";
                File.Copy(originalMsi, tmp, true);
                if (!MoveFileEx(tmp, target, MoveFileReplaceExisting | MoveFileWriteThrough))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                log($"POps: kurulum paketi geri dönüş için saklandı: {target}");
            }
            catch (Exception ex)
            {
                log("POps: kurulum paketi saklanamadı (güncelleme geri dönüşü dosya yedeğine düşer): " + ex.Message);
            }
        }

        // ------------------------------------------------------------------------------------------
        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // Yetenek politikası (ajanda AgentCapabilities): terminal ve Vision. Kurulum iki yönde de yazabilir; sunucu
        // yalnızca kapatabilir. Özellik verilmeyen bayrak mevcut dosyadan korunur, böylece sunucunun kapattığı yetenek
        // bir güncellemeyle kendiliğinden açılmaz. Dosya yoksa ikisi de açık başlar; var ama okunamıyorsa (ajan da
        // öyle sayar) verilmeyen bayrak kapalı kalır.
        private static void WriteCapabilities(Layout layout, bool? terminal, bool? vision, Action<string> log)
        {
            string path = Path.Combine(layout.SecureDir, CapabilitiesFile);
            bool exists = File.Exists(path);
            Dictionary<string, object> current = exists ? ReadJsonObject(path, log) : null;
            if (exists && terminal == null && vision == null) return;

            bool Keep(string key) => !exists || (current != null && (!current.TryGetValue(key, out object v) || !(v is bool b) || b));
            bool terminalEnabled = terminal ?? Keep("terminal_enabled");
            bool visionEnabled = vision ?? Keep("vision_enabled");

            var json = new Dictionary<string, object>
            {
                ["terminal_enabled"] = terminalEnabled,
                ["vision_enabled"] = visionEnabled,
                ["source"] = "msi",
                ["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            WriteProtected(path, ToJson(json, 0) + "\r\n");
            log($"POps: yetenekler yazıldı: terminal={(terminalEnabled ? "açık" : "kapalı")}, vision={(visionEnabled ? "açık" : "kapalı")}.");
        }

        // Karantina kilit politikaları (ajanda KioskMode): kaldırmada kayıttaki önceki değerlere dönülür, kayıt silinir.
        // Kovanı yüklü olmayan kullanıcının ayarı (karantinada oturumu kapatmış) geri alınamaz; loglanır (Agent/README).
        internal const string KioskRecordFile = "kiosk-policies.json";

        public static void RestoreKiosk(Layout layout, POps.Shared.IKioskRegistry registry, Action<string> log)
        {
            string path = Path.Combine(layout.SecureDir, KioskRecordFile);
            try
            {
                if (!File.Exists(path)) return;
                var record = Json.Deserialize<List<POps.Shared.KioskEntry>>(File.ReadAllText(path)) ?? new List<POps.Shared.KioskEntry>();
                List<POps.Shared.KioskEntry> pending = POps.Shared.KioskPolicies.Restore(registry, record);
                File.Delete(path);
                if (pending.Count == 0) log("POps: karantina kilit politikaları geri alındı.");
                else log($"POps: {pending.Count} kilit ayarı geri alınamadı (oturumu kapalı kullanıcı): {string.Join(", ", pending.Select(e => e.Hive + "\\" + e.Key + "\\" + e.Name))}. Elle temizlik: Agent/README.md.");
            }
            catch (Exception ex) { log("POps: karantina kilit politikaları geri alınamadı: " + ex.Message); }
        }

        // Kurum sertifikası (ajanda ServerTrust): SERVER_CA_CERT=<PEM yolu> dosyayı server-ca.pem olarak güvenli depoya
        // yazar, "system" siler, verilmezse mevcut dosya korunur. Yalnızca ilk sertifika bloğu alınır; CA olmayan
        // sertifika (BasicConstraints CA=false) reddedilir: ajan onunla zincir kuramaz ve hiçbir sunucuya bağlanamazdı.
        internal const string ServerCaFile = "server-ca.pem";

        private static string ReadServerCa(string value, out string pem, out bool remove, out string subject)
        {
            pem = null; remove = false; subject = null;
            if (value == null) return null;
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) { remove = true; return null; }
            if (!File.Exists(value)) return $"SERVER_CA_CERT dosyası bulunamadı: {value}";
            string text;
            try { text = File.ReadAllText(value); }
            catch (Exception ex) { return $"SERVER_CA_CERT dosyası okunamadı ({value}): {ex.Message}"; }
            const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
            int start = text.IndexOf(begin, StringComparison.Ordinal);
            int stop = start < 0 ? -1 : text.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0) return $"SERVER_CA_CERT geçerli bir PEM sertifikası değil ({value}): \"{begin}\" bloğu yok. Sunucudaki pops-ca.pem dosyasını verin (bkz. docs/tls.md).";
            string body = text.Substring(start + begin.Length, stop - start - begin.Length);
            X509Certificate2 cert;
            try { cert = new X509Certificate2(Convert.FromBase64String(Regex.Replace(body, @"\s+", ""))); }
            catch (Exception ex) { return $"SERVER_CA_CERT geçerli bir PEM sertifikası değil ({value}): {ex.Message}"; }
            foreach (X509Extension ext in cert.Extensions)
                if (ext is X509BasicConstraintsExtension bc && !bc.CertificateAuthority)
                    return $"SERVER_CA_CERT bir CA sertifikası değil ({cert.Subject}); sunucunun sertifikasını değil, onu imzalayan kurum sertifikasını (pops-ca.pem) verin.";
            subject = $"{cert.Subject}, parmak izi {cert.Thumbprint}";
            pem = begin + "\r\n" + Regex.Replace(Convert.ToBase64String(cert.RawData), ".{64}", "$0\r\n").TrimEnd() + "\r\n" + end + "\r\n";
            return null;
        }

        private static void WriteServerCa(Layout layout, string pem, bool remove, string subject, Action<string> log)
        {
            string path = Path.Combine(layout.SecureDir, ServerCaFile);
            if (remove)
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                log("POps: kurum sertifikası kaldırıldı; sunucu sertifikası sistem güven deposuyla doğrulanacak.");
                return;
            }
            if (pem == null) return;
            WriteProtected(path, pem);
            log($"POps: kurum sertifikası yazıldı ({subject}); sunucu sertifikası yalnızca onunla doğrulanacak.");
        }

        private static bool TryParseFlag(string value, out bool? flag)
        {
            flag = null;
            if (value == null) return true;
            switch (value.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": case "evet": flag = true; return true;
                case "0": case "false": case "no": case "off": case "hayir": case "hayır": flag = false; return true;
                default: return false;
            }
        }

        // Açık gelen değer her zaman yazılır; yoksa eski ayardaki değer, depoda henüz yoksa taşınır
        private static void WriteSecret(string path, string explicitValue, string legacyValue, string label, Action<string> log)
        {
            if (explicitValue != null)
            {
                WriteProtected(path, explicitValue);
                log($"POps: {label} güvenli depoya yazıldı.");
            }
            else if (legacyValue != null && !File.Exists(path))
            {
                WriteProtected(path, legacyValue);
                log($"POps: {label} eski ayar dosyasından güvenli depoya taşındı.");
            }
        }

        // ---- ACL (ajandaki SecureDataDirectory / SecureStore ile aynı) ----
        // Kurulumun çalıştığı hesap (LocalSystem). Birim testleri bunu testi çalıştıran kullanıcıya çevirir.
        internal static SecurityIdentifier SystemSid { get; set; } = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier AdminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier UsersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        // POpsData: SYSTEM/Administrators tam, Users okuma (tepsi ve watchdog identity.key'i okur).
        // POpsData\secure: yalnızca SYSTEM/Administrators.
        private static void EnsureDataDirectories(Layout layout)
        {
            var data = new DirectorySecurity();
            data.SetAccessRuleProtection(true, false);
            data.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            data.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            data.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
            CreateOrSecure(layout.DataDir, data);

            var secure = new DirectorySecurity();
            secure.SetAccessRuleProtection(true, false);
            secure.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            secure.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            CreateOrSecure(layout.SecureDir, secure);

            // C:\POpsLogs: SYSTEM olarak yazılan loglar; kullanıcılar okuyamaz, içine dosya/bağlantı bırakamaz
            // (tepsi ve watchdog loglarını %LOCALAPPDATA%\POps\Logs'a yazar)
            var logs = new DirectorySecurity();
            logs.SetAccessRuleProtection(true, false);
            logs.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            logs.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            if (layout.LogDir != null) CreateOrSecure(layout.LogDir, logs);
        }

        // ==========================================================================================
        // Kurulum klasörü (INSTALLFOLDER). Servis POpsAgent.exe'yi buradan SYSTEM olarak çalıştırır, updater buradan
        // kopyalanır: kullanıcılar ne klasöre yazabilmeli ne de onu (ya da bir üst klasörünü) silip/yeniden adlandırıp
        // yerine kendi klasörünü koyabilmeli.
        //  * CheckInstallFolder (immediate, dosyalardan önce): ağ yolu, sabit/NTFS olmayan birim, sürücü kökü, sistem
        //    klasörleri (ve üstleri) reddedilir; üst klasör zinciri denetlenir; klasörün kurulumdan önceki durumu
        //    belirlenir: new (yoktu), pops (boş ya da yalnızca bu paketin dosyaları), other (başka dosyalar var).
        //  * ApplyInstallFolderPolicy (Configure, SYSTEM): kullanıcıların yazabildiği klasör yalnızca POps'unsa
        //    (new/pops) daraltılır; başkasınınsa kurulum durur. Program Files gibi zaten korunan klasöre dokunulmaz.
        //    Sürücü kökünün, paylaşılan bir klasörün ya da başka programların izinleri böylece hiç değişmez.
        // ==========================================================================================
        public const string StateNew = "new", StatePops = "pops", StateOther = "other";

        // Yazma, silme ya da izin değiştirme sayılan haklar
        private const FileSystemRights WriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        // Klasörü silip yeniden adlandırabilme ya da izinlerini değiştirebilme
        private const FileSystemRights ReplaceRights = FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        // Kurulumun bu dosyalarından başka bir şey yoksa klasör POps'undur (eski sürüm kalıntıları dahil)
        private static readonly string[] PopsFilePatterns = { "appsettings*.json", "*.pdb", "PashaCoreAgent.*", "apply_update.bat", "POps*" };

        internal static IList<string> DefaultProtectedFolders()
        {
            var list = new List<string>();
            void Add(string path) { if (!string.IsNullOrWhiteSpace(path)) list.Add(path); }
            Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Add(Environment.GetEnvironmentVariable("WINDIR"));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            Add(Environment.GetEnvironmentVariable("ProgramFiles"));
            Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
            Add(Environment.GetEnvironmentVariable("ProgramW6432"));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            Add(Environment.GetEnvironmentVariable("ProgramData"));
            string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            Add(systemDrive.TrimEnd('\\') + @"\Users");
            return list;
        }

        // Hata metni ya da null; state: new / pops / other
        internal static string CheckInstallFolder(string installDir, IList<string> protectedFolders, ISet<string> knownFiles, out string state)
        {
            state = null;
            if (string.IsNullOrWhiteSpace(installDir)) return "INSTALLFOLDER boş.";
            string dir = installDir.Trim();
            if (dir.StartsWith(@"\\", StringComparison.Ordinal) || dir.StartsWith("//", StringComparison.Ordinal))
                return $"Kurulum klasörü ağ yolu olamaz ({dir}): POps yerel bir diske, ör. C:\\Program Files\\POps klasörüne kurulmalı.";
            if (!Path.IsPathRooted(dir) || !Regex.IsMatch(dir, @"^[A-Za-z]:\\"))
                return $"Kurulum klasörü tam bir yerel yol olmalı ({dir}), ör. C:\\Program Files\\POps.";
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); }
            catch (Exception ex) { return $"Kurulum klasörü geçersiz ({dir}): {ex.Message}"; }

            string root = Path.GetPathRoot(full);
            if (string.Equals(full + "\\", root, StringComparison.OrdinalIgnoreCase) || string.Equals(full, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return $"Kurulum klasörü bir sürücü kökü olamaz ({root}): POps'a ait ayrı bir klasör verin, ör. {root}Program Files\\POps.";

            DriveInfo drive;
            try { drive = new DriveInfo(root); }
            catch (Exception ex) { return $"Kurulum klasörünün sürücüsü okunamadı ({root}): {ex.Message}"; }
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                return $"Kurulum klasörü sabit bir yerel diskte olmalı ({root}: {drive.DriveType}).";
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return $"Kurulum klasörü NTFS bir diskte olmalı ({root}: {drive.DriveFormat}); izinler başka dosya sistemlerinde korunamaz.";

            foreach (string folder in protectedFolders ?? new string[0])
            {
                string protectedFull;
                try { protectedFull = Path.GetFullPath(folder).TrimEnd('\\'); }
                catch { continue; }
                if (SamePath(full, protectedFull) || protectedFull.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
                    return $"Kurulum klasörü bir sistem klasörü ya da onun üst klasörü olamaz ({full}): POps'a ait ayrı bir alt klasör verin, ör. {Path.Combine(protectedFull, "POps")}.";
            }

            string ancestorError = CheckAncestors(full);
            if (ancestorError != null) return ancestorError;

            if (!Directory.Exists(full)) { state = StateNew; return null; }
            state = OnlyPopsFiles(full, knownFiles) ? StatePops : StateOther;
            if (state == StateOther && Exposed(Directory.GetAccessControl(full)))
                return $"Kurulum klasöründe ({full}) başka dosyalar var ve kullanıcılar bu klasöre yazabiliyor. Servis buradan SYSTEM olarak çalışacağı için POps'u ayrı, yeni bir klasöre kurun (ör. C:\\Program Files\\POps).";
            return null;
        }

        // Yoldaki her üst klasör: kullanıcı onu (ya da içindeki alt klasörü) silip yeniden adlandıramamalı, izinlerini
        // değiştirememeli; henüz yoksa msiexec oluşturacağı için en yakın var olan üstten devralacağı haklara bakılır.
        // Birim testleri: zincir bu klasörde (hariç) durur. Testler kullanıcı profilindeki geçici klasörde çalışır; profilin
        // üst klasörlerinde uygulama paketlerinin (S-1-15-2-...) tam yetkisi vardır ve gerçekte oraya kurulum reddedilir.
        internal static string TrustedBaseForTests { get; set; }

        private static string CheckAncestors(string full)
        {
            string root = Path.GetPathRoot(full);
            string stop = TrustedBaseForTests != null ? Path.GetFullPath(TrustedBaseForTests).TrimEnd('\\') : null;
            bool underStop = stop != null && full.StartsWith(stop + "\\", StringComparison.OrdinalIgnoreCase);
            var chain = new List<string>();
            for (string p = Path.GetDirectoryName(full); p != null && !SamePath(p, root) && !(underStop && SamePath(p, stop)); p = Path.GetDirectoryName(p)) chain.Insert(0, p);

            string existing = underStop ? stop : root;
            if (!underStop && UntrustedHas(Directory.GetAccessControl(root), FileSystemRights.DeleteSubdirectoriesAndFiles, forChildren: false))
                return $"Kullanıcılar {root} içindeki klasörleri silebiliyor; POps bu diske güvenli kurulamaz.";
            foreach (string ancestor in chain)
            {
                if (!Directory.Exists(ancestor))
                {
                    if (UntrustedHas(Directory.GetAccessControl(existing), ReplaceRights | FileSystemRights.DeleteSubdirectoriesAndFiles, forChildren: true))
                        return $"{ancestor} kurulumla oluşturulunca kullanıcılar onu silip yeniden adlandırabilecek (izinleri {existing} klasöründen gelir). Üst klasörü önceden yalnızca yöneticilerin değiştirebileceği biçimde oluşturun ya da POps'u Program Files'a kurun.";
                    return null;
                }
                DirectorySecurity sec = Directory.GetAccessControl(ancestor);
                if (!IsTrusted(Owner(sec)) || UntrustedHas(sec, ReplaceRights | FileSystemRights.DeleteSubdirectoriesAndFiles, forChildren: false))
                    return $"Kullanıcılar üst klasörü ({ancestor}) silip yeniden adlandırabiliyor ya da izinlerini değiştirebiliyor; servis buradan SYSTEM olarak çalışacağı için POps oraya kurulamaz. Program Files'a ya da yalnızca yöneticilerin değiştirebildiği bir klasöre kurun.";
                existing = ancestor;
            }
            return null;
        }

        private static bool OnlyPopsFiles(string dir, ISet<string> knownFiles)
        {
            try
            {
                int count = 0;
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (++count > 10000) return false;
                    string name = Path.GetFileName(file);
                    if (knownFiles != null && knownFiles.Contains(name)) continue;
                    if (PopsFilePatterns.Any(p => MatchesPattern(name, p))) continue;
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool MatchesPattern(string name, string pattern)
        {
            string regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
            return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
        }

        // Kullanıcıların yazabildiği ya da sahibi güvenilir olmayan (izinlerini değiştirebilen) klasör
        internal static bool Exposed(DirectorySecurity sec) => UsersCanWrite(sec) || !IsTrusted(Owner(sec));

        // Configure (SYSTEM): hata metni ya da null
        internal static string ApplyInstallFolderPolicy(string installDir, string state, Action<string> log)
        {
            if (!Directory.Exists(installDir)) return null;
            if (!Exposed(Directory.GetAccessControl(installDir))) return null;   // zaten korunuyor (ör. Program Files): dokunulmaz
            if (state != StateNew && state != StatePops)
                return $"Kurulum klasörü ({installDir}) kullanıcıların yazabildiği bir yerde ve POps'a ait değil; izinleri değiştirilmedi. POps'u ayrı, yeni bir klasöre kurun (ör. C:\\Program Files\\POps).";
            try
            {
                var sec = new DirectorySecurity();
                sec.SetOwner(SystemSid);
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(installDir, sec);
            }
            catch (Exception ex)
            {
                return $"Kurulum klasörünün izinleri daraltılamadı ({installDir}): {ex.GetType().Name}: {ex.Message}. Klasör kullanıcıların yazabildiği bir yerde kaldığı için kurulum durduruldu.";
            }
            log?.Invoke($"[GÜVENLİK] {installDir} kullanıcıların yazabildiği bir klasördü ({state}); sahibi SYSTEM, izinleri SYSTEM/Administrators tam, Users okuma olarak daraltıldı.");
            return null;
        }

        // SYSTEM ve Administrators dışında yazma/değiştirme izni olan biri var mı (yalnızca klasörün kendisine uygulanan kurallar)
        internal static bool UsersCanWrite(DirectorySecurity sec) => UntrustedHas(sec, WriteRights, forChildren: false);

        // forChildren: false -> klasörün kendisine uygulanan kurallar; true -> yeni alt klasörlere geçecek kurallar
        private static bool UntrustedHas(DirectorySecurity sec, FileSystemRights rights, bool forChildren)
        {
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & rights) == 0) continue;
                bool applies = forChildren
                    ? (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0
                    : (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0;
                if (!applies) continue;
                var sid = rule.IdentityReference as SecurityIdentifier;
                // CREATOR OWNER yeni öğeyi oluşturana geçer; msiexec'in (SYSTEM) oluşturduğu klasörde SYSTEM olur
                if (sid != null && (IsTrusted(sid) || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid))) continue;
                return true;
            }
            return false;
        }

        private static SecurityIdentifier Owner(DirectorySecurity sec) => sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        // SYSTEM, Administrators ve hizmet SID'leri (NT SERVICE\TrustedInstaller: Program Files'ın olağan sahibi)
        private static bool IsTrusted(SecurityIdentifier sid) =>
            sid != null && (sid.Equals(SystemSid) || sid.Equals(AdminsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal));

        private static void CreateOrSecure(string dir, DirectorySecurity sec)
        {
            if (Directory.Exists(dir)) Directory.SetAccessControl(dir, sec);
            else Directory.CreateDirectory(dir, sec);
        }

        private static FileSecurity ProtectedFileSecurity()
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            return sec;
        }

        // İçerik yazılmadan önce korumalı ACL ile oluşturulur ve yerine atomik taşınır
        private static void WriteProtected(string path, string content)
        {
            string tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, ProtectedFileSecurity()))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (!MoveFileEx(tmp, path, MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                int error = Marshal.GetLastWin32Error();
                try { File.Delete(tmp); } catch { }
                throw new Win32Exception(error);
            }
        }

        // ---- silme (kilitli dosya yeniden başlatmada silinir) ----
        private static void DeleteTree(string dir, Action<string> log)
        {
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) DeleteFile(file, log);
            foreach (string sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length)) TryDeleteDirectory(sub, log);
            TryDeleteDirectory(dir, log);
        }

        private static void DeleteFile(string file, Action<string> log)
        {
            try
            {
                File.SetAttributes(file, System.IO.FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (MoveFileEx(file, null, MoveFileDelayUntilReboot))
                    log($"POps: {file} kullanımda, yeniden başlatmada silinecek.");
                else
                    log($"POps: {file} silinemedi: {ex.Message}");
            }
        }

        private static void TryDeleteDirectory(string dir, Action<string> log)
        {
            try { Directory.Delete(dir, false); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // İçinde yeniden başlatmada silinecek dosya kaldıysa klasör de onlardan sonra silinir
                if (!MoveFileEx(dir, null, MoveFileDelayUntilReboot))
                    log($"POps: {dir} silinemedi: {ex.Message}");
            }
        }

        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileDelayUntilReboot = 0x4;
        private const int MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        // ---- JSON (net472'de System.Text.Json yok; JavaScriptSerializer + girintili yazıcı) ----
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static Dictionary<string, object> ReadJsonObject(string path, Action<string> log)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                log($"POps: {path} okunamadı, yok sayıldı: {ex.Message}");
                return null;
            }
        }

        private static string StringValue(Dictionary<string, object> json, string key) =>
            json != null && json.TryGetValue(key, out object value) ? Clean(value as string) : null;

        internal static string ToJson(object value, int indent)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return Json.Serialize(s);
                case bool b: return b ? "true" : "false";
                case IDictionary<string, object> obj:
                    if (obj.Count == 0) return "{}";
                    var sb = new StringBuilder("{\r\n");
                    int i = 0;
                    foreach (KeyValuePair<string, object> kv in obj)
                    {
                        sb.Append(' ', indent + 2).Append(Json.Serialize(kv.Key)).Append(": ").Append(ToJson(kv.Value, indent + 2));
                        sb.Append(++i < obj.Count ? ",\r\n" : "\r\n");
                    }
                    return sb.Append(' ', indent).Append('}').ToString();
                case IEnumerable list:
                    var items = list.Cast<object>().Select(x => ToJson(x, indent + 2)).ToList();
                    if (items.Count == 0) return "[]";
                    return "[\r\n" + string.Join(",\r\n", items.Select(x => new string(' ', indent + 2) + x)) + "\r\n" + new string(' ', indent) + "]";
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }
    }
}
