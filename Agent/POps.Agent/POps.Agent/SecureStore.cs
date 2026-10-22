using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

#nullable disable

namespace POpsAgent
{
    // Gizli değerlerin (cihaz secret'ı, enroll jetonu, BypassSecret) tutulduğu dosyalar.
    // Klasör ve dosyaların ACL'i korumalıdır (üst klasörden izin devralmaz) ve yalnızca SYSTEM ile
    // Administrators'a açıktır: C:\POpsData öğrencilere okuma izni verse de buradakileri öğrenci
    // hesapları (Users) okuyamaz, listeleyemez. Kurulum ve güncelleme bu klasöre dokunmaz.
    [SupportedOSPlatform("windows")]
    public static class SecureStore
    {
        public const string DefaultDir = FolderSettings.DefaultDataDirectory + @"\secure";

        // Ajanın çalıştığı hesap (LocalSystem), süreç boyunca tek değer. Birim testleri bunu süreç başında bir kez testi
        // çalıştıran kullanıcıya çevirir: korumalı dosyayı değiştirmek için SYSTEM'in sahip olduğu hakları test kullanıcısı
        // üstlenir. Başka bir hesapla denetleyen test hesabı argüman olarak verir (Read).
        internal static SecurityIdentifier SystemSid { get; set; } = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier AdminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // Klasör ajanın AgentPaths.SecureDir'idir (servis: <veri klasörü>\secure; bkz. AgentDirectories); buradaki işlevler tam
        // yol alır.

        // Klasörü yoksa korumalı ACL ile oluşturur, varsa ACL'ini her açılışta yeniden kurar (bkz. FolderSettings.Secure).
        // Sahibi güvenilir değilse SYSTEM yapılır: klasörü önceden açan bir kullanıcı sahip olarak izinleri yeniden açabilirdi.
        public static void EnsureDirectory(string dir)
        {
            string error = FolderSettings.Secure(dir, usersRead: false, out bool tightened);
            if (error != null) throw new IOException($"{dir} kilitlenemedi: {error}");
            if (tightened) POpsHelpers.Log("SECURE", FolderSettings.TightenedNote(dir, usersRead: false));
        }

        public static FileSecurity ProtectedFileSecurity()
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            return sec;
        }

        // Dosya, içerik yazılmadan önce korumalı ACL ile oluşturulur ve yerine atomik taşınır;
        // yarım yazılmış ya da bir an için okunabilir bir dosya oluşmaz.
        public static void WriteProtected(string path, string content)
        {
            string tmp = path + ".tmp";
            File.Delete(tmp);
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            using (FileStream fs = new FileInfo(tmp).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, ProtectedFileSecurity()))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            try { File.Move(tmp, path, true); }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }

        // requireTrustedOwner: yalnızca sahibi SYSTEM ya da Administrators olan dosya okunur. Öğrencinin
        // yazabildiği bir klasörde (ör. dondurulmayan sürücü) önceden bırakılmış sahte dosya böylece
        // kabul edilmez. Korumalı klasörün kendisine zaten yalnızca SYSTEM/Administrators yazabilir.
        public static string Read(string path, bool requireTrustedOwner = false) => Read(path, requireTrustedOwner, SystemSid);

        // systemSid: güvenilen servis hesabı (ajan: SystemSid)
        internal static string Read(string path, bool requireTrustedOwner, SecurityIdentifier systemSid)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (requireTrustedOwner && !IsTrustedOwner(info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier, systemSid))
                {
                    POpsHelpers.Log("SECURE", $"[GÜVENLİK] {path} yok sayıldı: dosya sahibi SYSTEM/Administrators değil.", true);
                    return null;
                }
                string text = File.ReadAllText(path).Trim();
                return text.Length == 0 ? null : text;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("SECURE", $"{path} okunamadı: {ex.Message}", true);
                return null;
            }
        }

        public static void Delete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { POpsHelpers.Log("SECURE", $"{path} silinemedi: {ex.Message}", true); }
        }

        // DACL korumalı ve yalnızca SYSTEM/Administrators'a izin veriyorsa true.
        public static bool IsLockedDown(FileSystemSecurity sec)
        {
            if (!sec.AreAccessRulesProtected) return false;
            return sec.GetAccessRules(true, true, typeof(SecurityIdentifier))
                      .Cast<FileSystemAccessRule>()
                      .All(r => r.AccessControlType == AccessControlType.Deny
                                || r.IdentityReference.Equals(SystemSid)
                                || r.IdentityReference.Equals(AdminsSid));
        }

        private static bool IsTrustedOwner(SecurityIdentifier owner, SecurityIdentifier systemSid) =>
            owner != null && (owner.Equals(systemSid) || owner.Equals(AdminsSid));
    }
}
