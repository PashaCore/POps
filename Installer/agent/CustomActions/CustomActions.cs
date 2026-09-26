using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using WixToolset.Dtf.WindowsInstaller;

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

                string enrollToken = Prop("ENROLL_TOKEN");
                if (enrollToken != null && !TokenRegex.IsMatch(enrollToken))
                    return "ENROLL_TOKEN biçimi geçersiz; panelde üretilen jetonu olduğu gibi verin.";

                string persistDir = Prop("PERSIST_DIR") ?? Existing("PersistDir");
                if (persistDir != null && !Path.IsPathRooted(persistDir))
                    return "PERSIST_DIR tam bir klasör yolu olmalı (ör. T:\\POps).";

                EnsureDataDirectories(layout);
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
        private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
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
        }

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
