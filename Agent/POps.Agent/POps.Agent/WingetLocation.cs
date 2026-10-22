using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace POpsAgent
{
    // Bir kaldırma (Uninstall) kaydının bulunduğu yer
    public enum UninstallHive
    {
        // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall (64 bit görünüm)
        Machine,
        // HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall (32 bit uygulamalar)
        Machine32,
        // HKU\S-1-5-18\Software\Microsoft\Windows\CurrentVersion\Uninstall: SYSTEM hesabının kendi kayıtları
        SystemUser,
    }

    // "Programlar ve Özellikler"in bir kaydı: kurulduğu yerin kararına giren değerleriyle
    public sealed class UninstallEntry
    {
        public UninstallEntry(UninstallHive hive, string key)
        {
            Hive = hive;
            Key = key ?? throw new ArgumentNullException(nameof(key));
        }

        public UninstallHive Hive { get; }
        // Alt anahtarın adı (MSI'da ürün kodu, ör. {7AD362E6-020E-4F25-AD49-94A408DC5CA5})
        public string Key { get; }
        public string? DisplayName { get; init; }
        public string? DisplayVersion { get; init; }
        // WindowsInstaller = 1: kaydı Windows Installer yazdı
        public bool WindowsInstaller { get; init; }
        public string? InstallLocation { get; init; }
        public string? UninstallString { get; init; }
        public string? DisplayIcon { get; init; }

        public override string ToString() =>
            $"{Hive}\\{Key} ({LogText.Safe(DisplayName, 80)} {LogText.Safe(DisplayVersion, 40)})";
    }

    // Kurulumdan önce ya da sonra kayıt defterinin kurulu yazılım kayıtları (bkz. WingetLocation): üç Uninstall anahtarı ve
    // Windows Installer'ın ürün kayıtları (paketlenmiş ürün kodları). failure: okunamadı (kısa neden; karar belirlenemedi olur)
    public sealed class InstallRegistrySnapshot
    {
        public InstallRegistrySnapshot(IEnumerable<UninstallEntry> entries, IEnumerable<string> machineProducts, IEnumerable<string> systemProducts,
            string? failure = null)
        {
            Entries = (entries ?? throw new ArgumentNullException(nameof(entries))).ToList();
            MachineProducts = new HashSet<string>(machineProducts ?? throw new ArgumentNullException(nameof(machineProducts)), StringComparer.OrdinalIgnoreCase);
            SystemProducts = new HashSet<string>(systemProducts ?? throw new ArgumentNullException(nameof(systemProducts)), StringComparer.OrdinalIgnoreCase);
            Failure = failure;
        }

        public IReadOnlyList<UninstallEntry> Entries { get; }
        // HKLM\SOFTWARE\Classes\Installer\Products: makine geneli kurulan Windows Installer ürünleri
        public IReadOnlySet<string> MachineProducts { get; }
        // HKU\S-1-5-18\Software\Microsoft\Installer\Products: yalnızca SYSTEM hesabına (kullanıcı başına) kurulan ürünler
        public IReadOnlySet<string> SystemProducts { get; }
        public string? Failure { get; }

        // Hiç kayıt yok (testlerin varsayılan kapısı, bkz. MachineSeams.InstallRegistry)
        public static InstallRegistrySnapshot Nothing() =>
            new InstallRegistrySnapshot(Array.Empty<UninstallEntry>(), Array.Empty<string>(), Array.Empty<string>());

        public static InstallRegistrySnapshot Unreadable(string failure) =>
            new InstallRegistrySnapshot(Array.Empty<UninstallEntry>(), Array.Empty<string>(), Array.Empty<string>(), failure);
    }

    // Kurulduğu yer satırının kararı ve dayandığı kaldırma kayıtları (log için)
    public sealed class LocationDecision
    {
        public LocationDecision(string note, IReadOnlyList<UninstallEntry> entries)
        {
            Note = note;
            Entries = entries;
        }

        public string Note { get; }
        public IReadOnlyList<UninstallEntry> Entries { get; }
    }

    // winget paketinin kurulduğu yer (docs/agent.md "winget_install contract", 7). winget'in --scope'u ve "winget list"
    // yalnızca kaldırma kaydının kovanına bakar, bu da yanıltır: SYSTEM olarak kullanıcı başına kurulan bir MSI'ın (Anki
    // 26.09.3, saha testi 2026-10-22) dosyaları ve kısayolu SYSTEM'in profilindedir, ürün kaydı HKU\S-1-5-18'dedir, ama
    // Windows Installer kaldırma kaydını HKLM'e yazar; winget list --scope machine onu bulur. Karar bu yüzden winget'in
    // çıktısından değil, kurulumdan önce ve sonra okunan kayıt defterinden verilir:
    //  * Paketin kayıtları kurulumdan sonra yeni olan kaldırma kayıtlarıdır; yeni kayıt yoksa sürümü (DisplayVersion) değişenler
    //    (yerinde yükseltme). Aynı anda kendini güncelleyen başka bir program (ör. tarayıcı) yalnızca sürümünü değiştirir:
    //    yeni kayıt varken sayılmaz.
    //  * Her kayıt işaret verir. MSI (WindowsInstaller = 1, anahtar ürün kodu): ürün HKLM\SOFTWARE\Classes\Installer\Products'ta
    //    kayıtlıysa makine geneli, HKU\S-1-5-18\Software\Microsoft\Installer\Products'ta kayıtlıysa SYSTEM'in profili; MSI'ın
    //    kaldırma kaydının kovanı işaret sayılmaz. Kaydın HKU\S-1-5-18'de olması SYSTEM'in profilidir. InstallLocation,
    //    UninstallString ya da DisplayIcon: \System32 (ya da SysWOW64, Sysnative)\config\systemprofile\ altında bir yol SYSTEM'in
    //    profili, <sürücü>:\Program Files\ ya da Program Files (x86)\ altında bir yol makine genelidir. HKLM'deki MSI olmayan
    //    kaydın kovanı tek başına bir şey söylemez.
    //  * İşaretlerin hepsi aynı yeri gösteriyorsa o yer; ikisi birden, hiçbiri, paket zaten kuruluyken (yükleyici çalışmadı)
    //    ya da kayıt defteri okunamadıysa belirlenemedi. Tahmin edilmez.
    [SupportedOSPlatform("windows")]
    public static class WingetLocation
    {
        public const string SystemSid = "S-1-5-18";
        public const string MachineProductsPath = @"SOFTWARE\Classes\Installer\Products";
        public const string SystemProductsPath = SystemSid + @"\Software\Microsoft\Installer\Products";
        public const string SystemUninstallPath = SystemSid + @"\" + SoftwareInventory.UninstallPath;

        // belirlenemedi'nin kısa nedenleri (WingetInstall.UnknownLocationNote)
        public const string AlreadyInstalledReason = "paket zaten kuruluydu; yükleyici çalışmadı";
        public const string UnreadableReason = "kayıt defteri okunamadı";
        public const string NothingNewReason = "yeni ya da sürümü değişen kaldırma kaydı yok";
        public const string ConflictReason = "kayıtlar hem makine genelini hem SYSTEM'in profilini gösteriyor";
        public const string NoSignalReason = "kaldırma kaydı kurulduğu yeri göstermiyor";

        private static readonly Regex ProductCodeRegex = new Regex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // SYSTEM'in profili: %SystemRoot%\System32\config\systemprofile (32 bit süreçte WOW64 onu SysWOW64'e yönlendirir)
        private static readonly Regex SystemProfilePath = new Regex(@"\\(?:system32|syswow64|sysnative)\\config\\systemprofile(?:\\|""|,|\z)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        // Program Files (64 ve 32 bit; 8.3 kısa adları dahil), herhangi bir sürücüde
        private static readonly Regex ProgramFilesPath = new Regex(@"\b[a-z]:\\(?:program files(?: \(x86\))?|progra~\d)\\",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Windows Installer'ın paketlenmiş ürün kodu (Installer\Products altındaki anahtarın adı): ilk üç grup karakter karakter
        // ters çevrilir, son iki grubun her karakter çiftinin iki karakteri yer değiştirir; parantez ve tireler atılır.
        // {7AD362E6-020E-4F25-AD49-94A408DC5CA5} -> 6E263DA7E02052F4DA94494A80CDC55A. null: anahtar bir ürün kodu değil
        public static string? PackedProductCode(string? key)
        {
            if (key == null || !ProductCodeRegex.IsMatch(key)) return null;
            string[] groups = key.Substring(1, key.Length - 2).ToUpperInvariant().Split('-');
            var packed = new StringBuilder(32);
            for (int i = 0; i < 3; i++)
                for (int c = groups[i].Length - 1; c >= 0; c--) packed.Append(groups[i][c]);
            for (int i = 3; i < 5; i++)
                for (int c = 0; c < groups[i].Length; c += 2) packed.Append(groups[i][c + 1]).Append(groups[i][c]);
            return packed.ToString();
        }

        // Kurulumdan sonra yeni olan ve sürümü değişen kaldırma kayıtları (anahtarın adı büyük/küçük harfe duyarsız)
        public static (List<UninstallEntry> Added, List<UninstallEntry> Updated) Changes(InstallRegistrySnapshot before, InstallRegistrySnapshot after)
        {
            ArgumentNullException.ThrowIfNull(before);
            ArgumentNullException.ThrowIfNull(after);
            var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (UninstallEntry entry in before.Entries) versions[Identity(entry)] = entry.DisplayVersion;
            var added = new List<UninstallEntry>();
            var updated = new List<UninstallEntry>();
            foreach (UninstallEntry entry in after.Entries)
            {
                if (!versions.TryGetValue(Identity(entry), out string? version)) added.Add(entry);
                else if (!string.Equals(version, entry.DisplayVersion, StringComparison.Ordinal)) updated.Add(entry);
            }
            return (added, updated);
        }

        // Bir kaydın işaretleri: makine geneli mi, SYSTEM'in profili mi (ikisi birden olabilir: çelişki; hiçbiri: işaret yok)
        public static (bool MachineWide, bool SystemProfile) Signals(UninstallEntry entry, InstallRegistrySnapshot after)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(after);
            bool machine = false;
            bool system = entry.Hive == UninstallHive.SystemUser;
            string? packed = entry.WindowsInstaller ? PackedProductCode(entry.Key) : null;
            if (packed != null)
            {
                machine |= after.MachineProducts.Contains(packed);
                system |= after.SystemProducts.Contains(packed);
            }
            foreach (string? path in new[] { entry.InstallLocation, entry.UninstallString, entry.DisplayIcon })
            {
                if (string.IsNullOrEmpty(path)) continue;
                system |= SystemProfilePath.IsMatch(path);
                machine |= ProgramFilesPath.IsMatch(path);
            }
            return (machine, system);
        }

        // Kurulduğu yer satırı (WingetInstall.MachineWideNote, SystemProfileNote ya da UnknownLocationNote). exitCode: son
        // kurulum denemesinin kodu (yalnızca WingetInstall.IsOnThePc kodları); before/after: kurulumdan önceki ve sonraki kayıtlar
        public static LocationDecision Decide(int exitCode, InstallRegistrySnapshot before, InstallRegistrySnapshot after)
        {
            ArgumentNullException.ThrowIfNull(before);
            ArgumentNullException.ThrowIfNull(after);
            IReadOnlyList<UninstallEntry> none = Array.Empty<UninstallEntry>();
            // Zaten kuruluydu: winget yükleyiciyi çalıştırmadı, arada değişen kayıtlar pakete ait değildir
            if (WingetInstall.AlreadyInstalled(exitCode)) return new LocationDecision(WingetInstall.UnknownLocationNote(AlreadyInstalledReason), none);
            if (before.Failure != null || after.Failure != null) return new LocationDecision(WingetInstall.UnknownLocationNote(UnreadableReason), none);
            (List<UninstallEntry> added, List<UninstallEntry> updated) = Changes(before, after);
            List<UninstallEntry> entries = added.Count > 0 ? added : updated;
            if (entries.Count == 0) return new LocationDecision(WingetInstall.UnknownLocationNote(NothingNewReason), none);
            bool machine = false, system = false;
            foreach (UninstallEntry entry in entries)
            {
                (bool m, bool s) = Signals(entry, after);
                machine |= m;
                system |= s;
            }
            string note = machine && system ? WingetInstall.UnknownLocationNote(ConflictReason)
                : system ? WingetInstall.SystemProfileNote
                : machine ? WingetInstall.MachineWideNote
                : WingetInstall.UnknownLocationNote(NoSignalReason);
            return new LocationDecision(note, entries);
        }

        private static string Identity(UninstallEntry entry) => ((int)entry.Hive).ToString(CultureInfo.InvariantCulture) + "\\" + entry.Key;

        // Servisin kayıt defteri okuması (yalnızca MachineSeams.Production kurar, bkz. MachineSeams.InstallRegistry): üç Uninstall
        // anahtarının alt anahtarları ve iki ürün listesinin adları. Birkaç yüz anahtar, kurulumun yanında anlıktır. Olmayan anahtar
        // boştur (SYSTEM'in Uninstall anahtarı ilk kullanıcı başına kurulumdan önce yoktur); okuma hatası Failure olur.
        internal static InstallRegistrySnapshot ReadRegistry()
        {
            var entries = new List<UninstallEntry>();
            var machineProducts = new List<string>();
            var systemProducts = new List<string>();
            var failures = new List<string>();
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                ReadUninstall(hklm, SoftwareInventory.UninstallPath, UninstallHive.Machine, entries, failures);
                ReadUninstall(hklm, SoftwareInventory.UninstallPath32, UninstallHive.Machine32, entries, failures);
                ReadNames(hklm, MachineProductsPath, machineProducts, failures);
            }
            using (RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
            {
                ReadUninstall(users, SystemUninstallPath, UninstallHive.SystemUser, entries, failures);
                ReadNames(users, SystemProductsPath, systemProducts, failures);
            }
            return new InstallRegistrySnapshot(entries, machineProducts, systemProducts, failures.Count == 0 ? null : string.Join("; ", failures));
        }

        private static void ReadUninstall(RegistryKey root, string path, UninstallHive hive, List<UninstallEntry> entries, List<string> failures)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(path);
                if (key == null) return;
                foreach (string name in key.GetSubKeyNames())
                {
                    using RegistryKey? app = key.OpenSubKey(name);
                    // Arada silindi: kayıt yok
                    if (app == null) continue;
                    entries.Add(new UninstallEntry(hive, name)
                    {
                        DisplayName = Text(app, "DisplayName"),
                        DisplayVersion = Text(app, "DisplayVersion"),
                        WindowsInstaller = Text(app, "WindowsInstaller") == "1",
                        InstallLocation = Text(app, "InstallLocation"),
                        UninstallString = Text(app, "UninstallString"),
                        DisplayIcon = Text(app, "DisplayIcon"),
                    });
                }
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        private static void ReadNames(RegistryKey root, string path, List<string> names, List<string> failures)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(path);
                if (key != null) names.AddRange(key.GetSubKeyNames());
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        // Metin ya da sayı (WindowsInstaller DWORD'dür); boşsa null
        private static string? Text(RegistryKey key, string name)
        {
            string? text = Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
