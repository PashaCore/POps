using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

// Bu dosya hem ajanda (POps.Shared, net10.0) hem MSI custom action'larında (net472, bağlantılı derleme) kullanılır:
// yalnızca iki çerçevede de bulunan API'ler.
namespace POps.Shared
{
    // Seçilebilecek klasörlerin sınırları. Makinedeki varsayılanlar ForMachine; testler kendi kurallarını kurar (geçici
    // klasör kullanıcı profilinin içindedir ve üst klasörlerinin izinleri makineden makineye değişir).
    public sealed class FolderRules
    {
        // Klasörün kendisi, içi ve üst klasörleri olamaz: Windows, kullanıcı profilleri, kurulum klasörü
        public IList<string> Trees { get; } = new List<string>();
        // Klasörün kendisi ve üst klasörleri olamaz, içinde ayrı bir klasör olabilir: Program Files, ProgramData
        public IList<string> Roots { get; } = new List<string>();
        // Testler: üst klasör denetimi bu klasörde durur, altında yalnızca açıkça eklenen izinlere bakılır
        public string? TrustedBase { get; set; }

#if NET5_0_OR_GREATER
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
        public static FolderRules ForMachine(string? installDir)
        {
            var rules = new FolderRules();
            Add(rules.Trees, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Add(rules.Trees, Environment.GetEnvironmentVariable("WINDIR"));
            Add(rules.Trees, Environment.GetEnvironmentVariable("SystemRoot"));
            string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            Add(rules.Trees, systemDrive.TrimEnd('\\') + @"\Users");
            Add(rules.Trees, ProfilesDirectory());
            Add(rules.Trees, installDir);
            Add(rules.Roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Add(rules.Roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            Add(rules.Roots, Environment.GetEnvironmentVariable("ProgramFiles"));
            Add(rules.Roots, Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
            Add(rules.Roots, Environment.GetEnvironmentVariable("ProgramW6432"));
            Add(rules.Roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            Add(rules.Roots, Environment.GetEnvironmentVariable("ProgramData"));
            return rules;
        }

        private static void Add(IList<string> list, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) list.Add(path!);
        }

        // Kullanıcı profillerinin kökü (ProfileList\ProfilesDirectory; çoğunlukla C:\Users, başka bir diske taşınmış olabilir)
#if NET5_0_OR_GREATER
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
        private static string? ProfilesDirectory()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
                return key?.GetValue("ProfilesDirectory") is string value ? Environment.ExpandEnvironmentVariables(value) : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return null;
            }
        }
    }

    // Ajanın log ve veri klasörleri: appsettings.json "LogDirectory" ve "DataDirectory"; verilmezse C:\POpsLogs ve
    // C:\POpsData. Servis, updater, watchdog ve MSI aynı kurallarla karar verir:
    //  * Değer sürücü harfiyle başlayan tam bir yerel yol olmalı (ör. D:\POpsData). Göreli yol, ağ yolu (UNC), aygıt yolu
    //    (\\?\, \\.\), joker, alternatif veri akışı (':'), '.' / '..', ayrılmış ad (NUL, CON ...) ve 120 karakterden
    //    uzun yol reddedilir; sürücü kökü, Windows klasörü, kullanıcı profilleri, kurulum klasörü (içleri dahil),
    //    Program Files ve ProgramData'nın kendileri ve bunların üst klasörleri de. Klasör sabit bir NTFS (ya da ReFS)
    //    diskte olmalı ve var olan üst klasörlerini yalnızca yöneticiler silip yeniden adlandırabilmeli.
    //  * LogDirectory ile DataDirectory aynı klasör ya da biri ötekinin içi olamaz (log klasörü varsayılana döner).
    //  * Geçersiz ya da kilitlenemeyen klasörde varsayılana dönülür ve sorun bildirilir: ajan yine açılır.
    //  * Varsayılan klasörler bugünkü gibi kullanılır (denetlenmez); izinlerini çağıran kurar (Secure).
    // Klasör değişince eski klasördeki dosyalar taşınmaz (bkz. docs/configuration.md).
#if NET5_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    public sealed class FolderSettings
    {
        public const string DefaultDataDirectory = @"C:\POpsData";
        public const string DefaultLogDirectory = @"C:\POpsLogs";
        public const string DataDirectoryKey = "DataDirectory";
        public const string LogDirectoryKey = "LogDirectory";
        // Servisin updater'a (ikisi) ve watchdog'a (veri klasörü) geçirdiği klasörler
        public const string DataDirectorySwitch = "--datadir";
        public const string LogDirectorySwitch = "--logdir";
        // Alt klasörler (secure\clone-<zaman>, updater dosyaları) ve msiexec yolları MAX_PATH'e sığsın
        public const int MaxLength = 120;

        // Klasörleri oluşturan hesap (LocalSystem). Birim testleri bunu testi çalıştıran kullanıcıya çevirir.
        internal static SecurityIdentifier SystemSid { get; set; } = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier AdminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier UsersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        private const FileSystemRights ReadOnly = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        // Üst klasörü silip yeniden adlandırma, izinlerini değiştirme, sahiplenme ya da içindekileri silme
        private const FileSystemRights ReplaceRights = FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
                                                       | FileSystemRights.DeleteSubdirectoriesAndFiles;
        private const int GenericAll = 0x10000000, GenericExecute = 0x20000000, GenericWrite = 0x40000000, GenericRead = unchecked((int)0x80000000);

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private readonly string _defaultData;
        private readonly string _defaultLog;
        private readonly FolderRules _rules;

        private FolderSettings(string defaultData, string defaultLog, FolderRules rules)
        {
            _defaultData = Normalize(defaultData) ?? defaultData;
            _defaultLog = Normalize(defaultLog) ?? defaultLog;
            _rules = rules;
            DataDirectory = _defaultData;
            LogDirectory = _defaultLog;
        }

        public string DataDirectory { get; private set; }
        public string LogDirectory { get; private set; }
        public string SecureDirectory => Path.Combine(DataDirectory, "secure");
        public bool CustomData => !SamePath(DataDirectory, _defaultData);
        public bool CustomLog => !SamePath(LogDirectory, _defaultLog);
        // Geçersiz ayar ya da kilitlenemeyen klasör (varsayılana dönüldü)
        public IList<string> Problems { get; } = new List<string>();
        // Loglanacak bilgi: daraltılan izinler
        public IList<string> Notes { get; } = new List<string>();
        public string? Problem => Problems.Count == 0 ? null : string.Join("; ", Problems);

        // Ayardaki değerler (null ya da boş: varsayılan). checkLocation: disk ve üst klasör denetimi (watchdog yapmaz:
        // kullanıcı oturumunda çalışır, değeri servis zaten denetledi)
        public static FolderSettings Resolve(string? dataValue, string? logValue, FolderRules rules, bool checkLocation = true,
            string defaultData = DefaultDataDirectory, string defaultLog = DefaultLogDirectory)
        {
            var folders = new FolderSettings(defaultData, defaultLog, rules);
            folders.DataDirectory = folders.Pick(DataDirectoryKey, dataValue, folders._defaultData, checkLocation);
            folders.LogDirectory = folders.Pick(LogDirectoryKey, logValue, folders._defaultLog, checkLocation);
            folders.FixOverlap();
            return folders;
        }

        // Updater ve watchdog: servisin geçirdiği --datadir / --logdir (yoksa varsayılanlar), aynı kurallarla
        public static FolderSettings FromArguments(IList<string> args, FolderRules rules, bool checkLocation) =>
            Resolve(ArgumentValue(args, DataDirectorySwitch), ArgumentValue(args, LogDirectorySwitch), rules, checkLocation);

        public static string? ArgumentValue(IList<string> args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i + 1 < args.Count; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        // CreateProcessAsUser komut satırı için: denetlenmiş yolda tırnak ve sonda '\' yoktur
        public static string Argument(string name, string path) => name + " \"" + path + "\"";

        // Seçilen (varsayılan olmayan) klasörler oluşturulur ve kilitlenir; ardından üst klasörler yeniden denetlenir
        // (eksik olanlar az önce oluşturuldu). Olmazsa varsayılana dönülür. Varsayılan klasörleri çağıran kilitler.
        public void SecureCustom()
        {
            if (CustomData) DataDirectory = SecureOrDefault(DataDirectoryKey, DataDirectory, _defaultData, usersRead: true);
            if (CustomLog) LogDirectory = SecureOrDefault(LogDirectoryKey, LogDirectory, _defaultLog, usersRead: false);
            FixOverlap();
        }

        private string SecureOrDefault(string key, string dir, string fallback, bool usersRead)
        {
            string? error = Secure(dir, usersRead, out bool tightened) ?? CheckLocation(dir, _rules);
            if (error != null)
            {
                Problems.Add($"{key} klasörü ({dir}) kullanılamıyor: {error}; varsayılan {fallback} kullanılıyor");
                return fallback;
            }
            if (tightened) Notes.Add(TightenedNote(dir, usersRead));
            return dir;
        }

        private string Pick(string key, string? value, string fallback, bool checkLocation)
        {
            if (value == null || value.Trim().Length == 0) return fallback;
            string? error = CheckValue(value, _rules, out string full);
            if (error == null && SamePath(full, fallback)) return fallback;
            if (error == null && checkLocation) error = CheckLocation(full, _rules);
            if (error == null) return full;
            Problems.Add($"{key} geçersiz ({Shown(value)}): {error}; varsayılan {fallback} kullanılıyor");
            return fallback;
        }

        // Loga ve Olay Günlüğüne giden değer: denetim karakterleri '?', en çok 150 karakter
        private static string Shown(string value)
        {
            var text = new System.Text.StringBuilder();
            foreach (char c in value.Trim())
            {
                if (text.Length == 150) return text.Append('…').ToString();
                text.Append(c < ' ' ? '?' : c);
            }
            return text.ToString();
        }

        // Log klasörü veri klasörüyle aynı ya da biri ötekinin içiyse önce log klasörü, gerekirse veri klasörü varsayılana döner
        private void FixOverlap()
        {
            if (!Overlaps(DataDirectory, LogDirectory)) return;
            if (CustomLog)
            {
                Problems.Add($"{LogDirectoryKey} ({LogDirectory}) veri klasörüyle ({DataDirectory}) aynı ya da onun içi/üstü olamaz; varsayılan {_defaultLog} kullanılıyor");
                LogDirectory = _defaultLog;
            }
            if (Overlaps(DataDirectory, LogDirectory) && CustomData)
            {
                Problems.Add($"{DataDirectoryKey} ({DataDirectory}) log klasörüyle ({LogDirectory}) aynı ya da onun içi/üstü olamaz; varsayılan {_defaultData} kullanılıyor");
                DataDirectory = _defaultData;
            }
        }

        private static bool Overlaps(string a, string b) => SamePath(a, b) || IsInside(a, b) || IsInside(b, a);

        // ==========================================================================================
        // Değer (diske dokunmaz). Hata nedeni ya da null; fullPath: normalleştirilmiş yol (sonda '\' yok)
        // ==========================================================================================
        public static string? CheckValue(string? value, FolderRules rules, out string fullPath)
        {
            fullPath = "";
            string text = (value ?? "").Trim().Replace('/', '\\');
            if (text.Length == 0) return "boş";
            if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal) || text.StartsWith(@"\??\", StringComparison.Ordinal))
                return @"aygıt yolu (\\?\, \\.\) olamaz";
            if (text.StartsWith(@"\\", StringComparison.Ordinal))
                return "ağ yolu (UNC) olamaz; yerel bir diskteki klasörü verin";
            if (text.Length < 3 || !IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\')
                return @"sürücü harfiyle başlayan tam bir yol olmalı (ör. D:\POpsData)";
            if (text.Length > MaxLength) return $"en çok {MaxLength} karakter olabilir";
            foreach (char c in text)
                if (c < ' ' || c == '*' || c == '?' || c == '<' || c == '>' || c == '|' || c == '"')
                    return "joker ya da yolda geçersiz bir karakter (* ? < > | \" ya da denetim karakteri) içeremez";
            if (text.IndexOf(':', 2) >= 0) return "sürücü harfinden sonra ':' içeremez (alternatif veri akışı)";
            foreach (string segment in text.Substring(3).Split('\\'))
            {
                if (segment.Length == 0) continue;
                if (segment == "." || segment == "..") return "'.' ya da '..' içeremez";
                char last = segment[segment.Length - 1];
                if (last == '.' || last == ' ') return $"klasör adı nokta ya da boşlukla bitemez ({segment})";
                int dot = segment.IndexOf('.');
                if (ReservedNames.Contains((dot < 0 ? segment : segment.Substring(0, dot)).Trim())) return $"ayrılmış bir ad içeremez ({segment})";
            }

            string? full = Normalize(text);
            if (full == null) return "geçersiz yol";
            if (full.Length <= 2) return @"sürücü kökü olamaz; içinde ayrı bir klasör verin (ör. D:\POpsData)";
            var trees = new List<string>();
            foreach (string tree in rules.Trees)
            {
                string? protectedTree = Normalize(tree);
                if (protectedTree == null) continue;
                if (SamePath(full, protectedTree) || IsInside(full, protectedTree)) return $"{protectedTree} ya da içindeki bir klasör olamaz";
                trees.Add(protectedTree);
            }
            foreach (string root in rules.Roots)
            {
                string? protectedRoot = Normalize(root);
                if (protectedRoot == null) continue;
                if (SamePath(full, protectedRoot)) return $"{protectedRoot} olamaz; içinde ayrı bir klasör verin (ör. {Path.Combine(protectedRoot, "POpsData")})";
                trees.Add(protectedRoot);
            }
            // Üst klasörü olamaz: izinleri (Users'ın okuyamaması dahil) korunan klasöre de geçerdi
            foreach (string protectedFolder in trees)
                if (IsInside(protectedFolder, full)) return $"{protectedFolder} klasörünün üst klasörü olamaz";
            fullPath = full;
            return null;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        // ==========================================================================================
        // Konum (diski okur, değiştirmez): sürücü ve var olan üst klasörler. Hata nedeni ya da null.
        // ==========================================================================================
        public static string? CheckLocation(string fullPath, FolderRules rules)
        {
            string root = Path.GetPathRoot(fullPath) ?? "";
            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return $"{root} sürücüsü yok ya da hazır değil";
                if (drive.DriveType != DriveType.Fixed) return $"sabit bir yerel diskte olmalı ({root}: {drive.DriveType})";
                string format = drive.DriveFormat;
                if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase) && !string.Equals(format, "ReFS", StringComparison.OrdinalIgnoreCase))
                    return $"NTFS bir diskte olmalı ({root}: {format}); izinler başka dosya sistemlerinde korunamaz";
                return CheckParents(fullPath, root, rules);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                return $"sürücü ya da üst klasörlerin izinleri okunamadı: {ex.Message}";
            }
        }

        // Var olan her üst klasör: güvenilmeyen bir hesap onu silemez, yeniden adlandıramaz, izinlerini değiştiremez,
        // sahiplenemez, içindekileri silemez; sahibi güvenilir. Yoksa öğrenci üst klasörü kenara alıp yerine kendi
        // klasörünü koyabilir, servisin (SYSTEM) oradan çalıştırdığı updater'ı ya da okuduğu dosyaları değiştirebilirdi.
        // Sürücü kökünde yalnızca içindekileri silme hakkına bakılır. Eksik üst klasörleri Secure korumalı oluşturur.
        private static string? CheckParents(string fullPath, string root, FolderRules rules)
        {
            string? stop = rules.TrustedBase == null ? null : Normalize(rules.TrustedBase);
            bool underStop = stop != null && IsInside(fullPath, stop);
            if (!underStop && UntrustedHas(new DirectoryInfo(root).GetAccessControl(), FileSystemRights.DeleteSubdirectoriesAndFiles, explicitOnly: false))
                return $"kullanıcılar {root} içindeki klasörleri silebiliyor";
            for (string? parent = Path.GetDirectoryName(fullPath); parent != null && !SamePath(parent, root) && !(underStop && SamePath(parent, stop!));
                 parent = Path.GetDirectoryName(parent))
            {
                var info = new DirectoryInfo(parent);
                if (!info.Exists) continue;
                DirectorySecurity sec = info.GetAccessControl();
                if (!IsTrusted(Owner(sec)) || UntrustedHas(sec, ReplaceRights, explicitOnly: underStop))
                    return $"kullanıcılar üst klasörü ({parent}) silip yeniden adlandırabiliyor ya da izinlerini değiştirebiliyor; yalnızca yöneticilerin değiştirebildiği bir yer seçin";
            }
            return null;
        }

        // ==========================================================================================
        // İzinler
        // ==========================================================================================
        // Klasörü (ve eksik üst klasörlerini) korumalı izinle oluşturur ya da izinlerini yeniden kurar, sonra geri okuyup
        // doğrular. SYSTEM ve Administrators tam; usersRead ise Users okuma (watchdog update.lock'u okur); üst klasörden
        // izin devralınmaz. Sahibi güvenilir değilse (klasörü bir öğrenci önceden açtıysa izinleri yeniden açabilirdi)
        // sahibi SYSTEM yapılır. Eksik üst klasörler SYSTEM/Administrators tam, Users okuma ile oluşturulur.
        // tightened: klasör vardı ve başka hesaplara açıktı ya da sahibi güvenilir değildi. Hata nedeni ya da null.
        public static string? Secure(string dir, bool usersRead, out bool tightened)
        {
            tightened = false;
            try
            {
                var info = new DirectoryInfo(dir);
                CreateMissing(info.Parent);
                DirectorySecurity wanted = Protected(usersRead);
                if (!info.Exists)
                {
                    info.Create(wanted);
                }
                else
                {
                    DirectorySecurity current = info.GetAccessControl();
                    tightened = Loose(current, usersRead);
                    if (!IsTrusted(Owner(current))) wanted.SetOwner(SystemSid);
                    info.SetAccessControl(wanted);
                }
                DirectorySecurity result = new DirectoryInfo(dir).GetAccessControl();
                if (!result.AreAccessRulesProtected || Loose(result, usersRead))
                    return "SYSTEM/Administrators dışında erişim izni ya da güvenilmeyen bir sahip kaldı";
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                                       || ex is NotSupportedException || ex is InvalidOperationException || ex is PrivilegeNotHeldException)
            {
                return ex.Message;
            }
        }

        public static string TightenedNote(string dir, bool usersRead) =>
            $"[GÜVENLİK] {dir} başka hesaplara açıktı ya da sahibi güvenilir değildi; izinleri daraltıldı (SYSTEM/Administrators tam{(usersRead ? ", Users okuma" : "")}, devralma yok).";

        private static void CreateMissing(DirectoryInfo? dir)
        {
            if (dir == null || dir.Exists) return;
            CreateMissing(dir.Parent);
            dir.Create(Protected(usersRead: true));
        }

        private static DirectorySecurity Protected(bool usersRead)
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            if (usersRead) sec.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
            return sec;
        }

        // Güvenilmeyen sahip ya da SYSTEM/Administrators dışındaki herhangi bir izin (usersRead: Users'ın okuma/çalıştırma hakkı hariç)
        private static bool Loose(DirectorySecurity sec, bool usersRead)
        {
            if (!IsTrusted(Owner(sec))) return true;
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var sid = rule.IdentityReference as SecurityIdentifier;
                if (IsTrusted(sid)) continue;
                if (usersRead && sid != null && UsersSid.Equals(sid) && (Effective(rule.FileSystemRights) & ~ReadOnly) == 0) continue;
                return true;
            }
            return false;
        }

        // Klasörün kendisine uygulanan, güvenilmeyen bir hesaba verilmiş haklar (CREATOR OWNER yeni öğeyi oluşturana geçer)
        private static bool UntrustedHas(DirectorySecurity sec, FileSystemRights rights, bool explicitOnly)
        {
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, !explicitOnly, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if ((Effective(rule.FileSystemRights) & rights) == 0) continue;
                var sid = rule.IdentityReference as SecurityIdentifier;
                if (IsTrusted(sid) || (sid != null && sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid))) continue;
                return true;
            }
            return false;
        }

        // Genel (GENERIC_*) haklar dosya haklarına çevrilir: yoksa GENERIC_ALL veren bir kural hiç hak vermiyor görünürdü
        private static FileSystemRights Effective(FileSystemRights rights)
        {
            int value = (int)rights;
            if ((value & GenericAll) != 0) return FileSystemRights.FullControl;
            var mapped = (FileSystemRights)(value & 0x0FFFFFFF);
            if ((value & GenericWrite) != 0) mapped |= FileSystemRights.Write;
            if ((value & (GenericRead | GenericExecute)) != 0) mapped |= FileSystemRights.ReadAndExecute;
            return mapped;
        }

        private static SecurityIdentifier? Owner(DirectorySecurity sec) => sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        // SYSTEM, Administrators ve hizmet SID'leri (NT SERVICE\TrustedInstaller: Program Files'ın olağan sahibi)
        private static bool IsTrusted(SecurityIdentifier? sid) =>
            sid != null && (sid.Equals(SystemSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.Equals(AdminsSid)
                            || sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal));

        // ==========================================================================================
        // Yol karşılaştırma
        // ==========================================================================================
        public static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path!.Trim()).TrimEnd('\\'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        public static bool SamePath(string a, string b) =>
            string.Equals((a ?? "").TrimEnd('\\'), (b ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static bool IsInside(string child, string parent) =>
            child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
