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
        public const string DefaultDir = @"C:\POpsData\secure";

        private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier AdminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        public static string Dir { get; set; } = DefaultDir;

        public static string PathOf(string name) => Path.Combine(Dir, name);

        // Klasörü yoksa korumalı ACL ile oluşturur, varsa ACL'ini her açılışta yeniden kurar.
        public static void EnsureDirectory()
        {
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            var dir = new DirectoryInfo(Dir);
            if (dir.Exists) dir.SetAccessControl(sec);
            else dir.Create(sec);
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
        public static string Read(string path, bool requireTrustedOwner = false)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (requireTrustedOwner && !IsTrustedOwner(info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier))
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

        private static bool IsTrustedOwner(SecurityIdentifier owner) =>
            owner != null && (owner.Equals(SystemSid) || owner.Equals(AdminsSid));
    }
}
