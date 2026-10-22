#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

// Bu dosya hem ajanda (POps.Shared, net10.0) hem MSI custom action'larında (net472, bağlantılı derleme) kullanılır:
// yalnızca iki çerçevede de bulunan API'ler.
namespace POps.Shared
{
    // Karantina kilit ekranı sürerken Ctrl+Alt+Del ekranının sunduğu kaçış yolları kapatılır. Güvenli dikkat dizisi
    // (SAS) uygulamayla engellenemez; ama sunduğu seçenekler politikayla kaldırılabilir:
    //  * DisableTaskMgr (Görev Yöneticisi; Ctrl+Shift+Esc ve "taskmgr" de), NoLogoff (oturumu kapat),
    //    HideFastUserSwitching (kullanıcı değiştir), DisableChangePassword (parola değiştir),
    //    DisableLockWorkstation (bilgisayarı kilitle).
    //  * Makine (HKLM): HideFastUserSwitching ve DisableTaskMgr, açık oturumda da anında. Oturum açmış her kullanıcının
    //    kovanı (HKU\<SID>): DisableTaskMgr, DisableLockWorkstation, DisableChangePassword, NoLogoff; son üçü o
    //    kullanıcının bir sonraki oturum açılışında etkili olur (açık oturumda görünmeye devam eder, ama kilidi kaldırmaz:
    //    oturumu kapatıp açan öğrenci yine kilit ekranına döner, ağ yalıtımı sürer).
    // Kurallar: önceki değer ÖNCE kaydedilir (C:\POpsData\secure\kiosk-policies.json), sonra 1 yazılır. Geri alırken
    // yalnızca hâlâ bizim yazdığımız değer (1) duruyorsa önceki değere dönülür; önceden de 1 olan (kurumun kendi
    // politikası) 1 kalır, bizden sonra başkasının değiştirdiği değere dokunulmaz. Kovanı yüklü olmayan kullanıcının
    // (karantinada oturumu kapattı) kaydı bekletilir; yeniden oturum açınca geri alınır.
    public sealed class KioskEntry
    {
        public string Hive { get; set; }
        public string Key { get; set; }
        public string Name { get; set; }
        public bool HadValue { get; set; }
        public int PreviousValue { get; set; }
    }

    public interface IKioskRegistry
    {
        // Yüklü kullanıcı kovanları: "HKU\S-1-5-21-..."
        IList<string> UserHives();
        int? Get(string hive, string key, string name);
        void Set(string hive, string key, string name, int value);
        void Delete(string hive, string key, string name);
    }

    public static class KioskPolicies
    {
        public const string Machine = "HKLM";
        public const string SystemKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
        public const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

        private static readonly string[][] UserValues =
        {
            new[] { SystemKey, "DisableTaskMgr" },
            new[] { SystemKey, "DisableLockWorkstation" },
            new[] { SystemKey, "DisableChangePassword" },
            new[] { ExplorerKey, "NoLogoff" },
        };

        // Sahada ölçüldü (Windows 11, 2026-09-29): HKLM'de yalnızca bu ikisi etkili ve ANINDA etkili. Kilitle, oturumu
        // kapat ve parola değiştir yalnızca kullanıcı politikasıdır ve Winlogon onları oturum açılışında okur (gpupdate
        // de yenilemez): karantina sırasında açılan oturumlarda (yeniden başlatma sonrası dahil) gizlenirler.
        private static readonly string[][] MachineValues =
        {
            new[] { SystemKey, "HideFastUserSwitching" },
            new[] { SystemKey, "DisableTaskMgr" },
        };

        public static List<KioskEntry> Targets(IEnumerable<string> userHives)
        {
            var targets = MachineValues.Select(v => new KioskEntry { Hive = Machine, Key = v[0], Name = v[1] }).ToList();
            foreach (string hive in userHives ?? Enumerable.Empty<string>())
                targets.AddRange(UserValues.Select(v => new KioskEntry { Hive = hive, Key = v[0], Name = v[1] }));
            return targets;
        }

        private static bool Same(KioskEntry a, KioskEntry b) =>
            string.Equals(a.Hive, b.Hive, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        // 1) Kayda henüz olmayan hedefler, ŞU ANKİ değerleriyle eklenir (kayıttakinin önceki değeri korunur). Yazmaz.
        public static List<KioskEntry> Plan(IKioskRegistry registry, IEnumerable<KioskEntry> record)
        {
            var result = (record ?? Enumerable.Empty<KioskEntry>()).ToList();
            foreach (KioskEntry target in Targets(registry.UserHives()))
            {
                if (result.Any(e => Same(e, target))) continue;
                int? previous = registry.Get(target.Hive, target.Key, target.Name);
                target.HadValue = previous.HasValue;
                target.PreviousValue = previous ?? 0;
                result.Add(target);
            }
            return result;
        }

        // 2) Kayıt diske yazıldıktan SONRA: yüklü kovanlardaki bütün hedeflere 1. Dönen: yazılamayanların açıklaması.
        public static List<string> Enforce(IKioskRegistry registry, IEnumerable<KioskEntry> record)
        {
            var loaded = Loaded(registry);
            var errors = new List<string>();
            foreach (KioskEntry e in record)
            {
                if (!loaded.Contains(e.Hive)) continue;
                try { if (registry.Get(e.Hive, e.Key, e.Name) != 1) registry.Set(e.Hive, e.Key, e.Name, 1); }
                catch (Exception ex) { errors.Add($"{e.Hive}\\{e.Key}\\{e.Name}: {ex.Message}"); }
            }
            return errors;
        }

        // Geri alma. Dönen: şimdi geri alınamayan (kovanı yüklü değil ya da yazılamadı) kayıtlar; kayıtta kalırlar.
        public static List<KioskEntry> Restore(IKioskRegistry registry, IEnumerable<KioskEntry> record)
        {
            var loaded = Loaded(registry);
            var pending = new List<KioskEntry>();
            foreach (KioskEntry e in record ?? Enumerable.Empty<KioskEntry>())
            {
                if (!loaded.Contains(e.Hive)) { pending.Add(e); continue; }
                try
                {
                    // Yalnızca bizim yazdığımız değer: sonradan başkası değiştirdiyse ya da silindiyse dokunulmaz
                    if (registry.Get(e.Hive, e.Key, e.Name) != 1) continue;
                    if (!e.HadValue) registry.Delete(e.Hive, e.Key, e.Name);
                    else if (e.PreviousValue != 1) registry.Set(e.Hive, e.Key, e.Name, e.PreviousValue);
                }
                catch (Exception) { pending.Add(e); }
            }
            return pending;
        }

        private static HashSet<string> Loaded(IKioskRegistry registry)
        {
            var loaded = new HashSet<string>(registry.UserHives() ?? new List<string>(), StringComparer.OrdinalIgnoreCase) { Machine };
            return loaded;
        }
    }

    // Gerçek kayıt defteri (64 bit görünüm). HKU\<SID>: yalnızca yerel/etki alanı kullanıcıları (S-1-5-21), _Classes hariç.
#if NET5_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    public sealed class WindowsKioskRegistry : IKioskRegistry
    {
        private static RegistryKey Root(string hive, out string prefix)
        {
            prefix = null;
            if (string.Equals(hive, KioskPolicies.Machine, StringComparison.OrdinalIgnoreCase))
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (hive != null && hive.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase))
            {
                prefix = hive.Substring(4) + "\\";
                return RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            }
            throw new ArgumentException("Bilinmeyen kovan: " + hive);
        }

        public IList<string> UserHives()
        {
            using (RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
                return users.GetSubKeyNames()
                    .Where(n => n.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    .Select(n => @"HKU\" + n).ToList();
        }

        public int? Get(string hive, string key, string name)
        {
            using (RegistryKey root = Root(hive, out string prefix))
            using (RegistryKey k = root.OpenSubKey(prefix + key))
                return k?.GetValue(name) is int value ? value : (int?)null;
        }

        public void Set(string hive, string key, string name, int value)
        {
            using (RegistryKey root = Root(hive, out string prefix))
            using (RegistryKey k = root.CreateSubKey(prefix + key))
                k.SetValue(name, value, RegistryValueKind.DWord);
        }

        public void Delete(string hive, string key, string name)
        {
            using (RegistryKey root = Root(hive, out string prefix))
            using (RegistryKey k = root.OpenSubKey(prefix + key, true))
                k?.DeleteValue(name, false);
        }
    }
}
