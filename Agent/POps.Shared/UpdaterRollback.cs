#nullable disable
using System;

namespace POps.Shared
{
    // POpsUpdater'ın MSI ile geri dönüşü: msiexec komutları, geri kurulumun gerçekten tuttuğunun denetimi ve
    // sunucuya giden sonuç (outcome / rollback / detail). Makineye dokunan her adım Steps üzerinden verilir;
    // mantık birim testlerinde sahte adımlarla sınanır.
    //
    // 0.1.6'ya kadar geri kurulum sessizce ajansız bırakıyordu (0.1.4 -> 0.1.5 ve 0.1.5 -> 0.1.6 tatbikatları,
    // DESKTOP-JBSQMIH'de elle yeniden üretildi): Windows Installer maliyet hesabını yeni sürüm hâlâ kuruluyken
    // yapar; sürümü düşük dosyaların bileşenlerini "Disallowing installation of component ... since the same
    // component with higher versioned keyfile exists" diyerek kurmaz (AgentService: Request Local, Action Null),
    // RemoveExistingProducts sonra yeni sürümü servisiyle birlikte kaldırır. msiexec 0 döner, ürün kaydı eski
    // sürümdedir ama POpsAgent servisi ve sürümlü POps dosyaları yoktur. REINSTALLMODE=amus ("a": sürümüne
    // bakmadan bütün dosyaları yaz) bileşenlerin kurulmasını sağlar; yine de msiexec 0 döndükten sonra ajanın
    // yerinde olduğu denetlenir, değilse aynı paketle hemen onarılır.
    public static class UpdaterRollback
    {
        // Geri kurulum: önceki paket, kurulu yeni sürümü aynı Windows Installer işlemi içinde kaldırıp kendini kurar.
        // POPS_ROLLBACK=1 sürüm düşürme engelini yalnızca bu çağrı için açar.
        public static string InstallArguments(string package, string installFolderArg) =>
            $"/i \"{package}\" /qn /norestart REBOOT=ReallySuppress POPS_ROLLBACK=1 REINSTALLMODE=amus{installFolderArg}";

        // Onarım: paketin ürünü kuruluysa bütün dosyaları sürümüne bakmadan yeniden yazar, servisi yeniden kurar
        // (/fvamus; sahada ajanı hep bu geri getirdi). Kurulu değilse aynı paket geri kurulum olarak yeniden kurulur.
        public static string RepairArguments(string package, bool productInstalled, string installFolderArg) =>
            productInstalled
                ? $"/fvamus \"{package}\" /qn /norestart REBOOT=ReallySuppress"
                : InstallArguments(package, installFolderArg);

        // Ajan yerinde mi: null = servis var ve POpsAgent.exe beklenen sürümde; değilse eksik olanın açıklaması.
        // exeFileVersion: POpsAgent.exe'nin FileVersion'ı ("0.1.5.0"), dosya yoksa null.
        public static string MissingAgent(bool serviceExists, string exeFileVersion, string expectedVersion)
        {
            if (!serviceExists && exeFileVersion == null) return "POpsAgent servisi ve POpsAgent.exe yok";
            if (!serviceExists) return "POpsAgent servisi yok";
            if (exeFileVersion == null) return "POpsAgent.exe yok";
            if (!SameRelease(exeFileVersion, expectedVersion)) return $"POpsAgent.exe {exeFileVersion} (beklenen {expectedVersion})";
            return null;
        }

        // FileVersion ("0.1.5.0") ile sürüm etiketi ("0.1.5-alpha", "v0.1.5-alpha") aynı sürüm mü: ilk üç alan
        public static bool SameRelease(string fileVersion, string version)
        {
            int[] a = ReleaseNumbers(fileVersion), b = ReleaseNumbers(version);
            return a != null && b != null && a[0] == b[0] && a[1] == b[1] && a[2] == b[2];
        }

        static int[] ReleaseNumbers(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            string core = version.Trim().TrimStart('v', 'V');
            int cut = core.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) core = core.Substring(0, cut);
            string[] parts = core.Split('.');
            if (parts.Length < 3) return null;
            var numbers = new int[3];
            for (int i = 0; i < 3; i++)
                if (!int.TryParse(parts[i], out numbers[i])) return null;
            return numbers;
        }

        // Updater'ın makineye dokunan adımları
        public sealed class Steps
        {
            public Func<string, string, int> RunMsiexec;        // (argümanlar, log adı) -> çıkış kodu
            public Func<bool> ServiceExists;
            public Func<string> AgentExeVersion;                // servisin çalıştırdığı POpsAgent.exe'nin FileVersion'ı; yoksa null
            public Func<bool> PackageProductInstalled;          // geri kurulan paketin ProductCode'u kurulu mu
            public Action EnsureServiceRunning;
            public Func<bool> WaitForHealth;                    // geri kurulan sürüm health.json yazdı mı (90 sn)
            public Action<TimeSpan> Sleep;
            public Action<string, bool> Log;
        }

        public static (string Outcome, string Rollback, string Detail) RunMsi(Steps steps, string package, string installFolderArg, string from, string to)
        {
            int exit = -1;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                exit = steps.RunMsiexec(InstallArguments(package, installFolderArg), $"rollback-{from}-{attempt}");
                if (exit == 0 || exit == 3010) break;
                if (attempt == 1)
                {
                    steps.Log($"Geri kurulum {exit} döndü; 30 sn sonra bir kez daha denenecek.", true);
                    steps.Sleep(TimeSpan.FromSeconds(30));
                }
            }
            if (exit == 3010)
                return ("rollback_pending_reboot", "msi", $"{to} sağlıklı açılmadı; {from} kuruldu, yeniden başlatmada tamamlanacak");
            if (exit != 0)
                return ("rollback_failed", "msi", $"{to} sağlıklı açılmadı; geri kurulum {exit} döndü ve Windows Installer {to} sürümünü yerinde bıraktı");

            // msiexec 0 döndü: sağlık için 90 sn beklemeden önce ajanın gerçekten yerinde olduğu denetlenir
            string missing = MissingAgent(steps.ServiceExists(), steps.AgentExeVersion(), from);
            if (missing == null)
            {
                if (steps.WaitForHealth())
                    return ("rolled_back", "msi", $"{to} sağlıklı açılmadı; {from} geri kuruldu");
                return ("rollback_failed", "msi", $"{to} sağlıklı açılmadı; {from} geri kuruldu ama o da sağlıklı açılmadı");
            }

            steps.Log($"Geri kurulum 0 döndü ama {missing}; aynı paketle hemen onarılıyor.", true);
            int repairExit = steps.RunMsiexec(RepairArguments(package, steps.PackageProductInstalled(), installFolderArg), $"rollback-repair-{from}");
            steps.EnsureServiceRunning();
            string stillMissing = MissingAgent(steps.ServiceExists(), steps.AgentExeVersion(), from);
            string installed = $"{to} sağlıklı açılmadı; {from} geri kurulumu 0 döndü ama {missing}";
            if (stillMissing != null)
                return ("rollback_failed", "msi_repair", $"{installed}; aynı paketle onarım ({repairExit}) sonrasında da {stillMissing}");
            if (steps.WaitForHealth())
                return ("rolled_back", "msi_repair", $"{installed}; aynı paketle onarıldı ({repairExit}) ve {from} sağlıklı açıldı");
            return ("rollback_failed", "msi_repair", $"{installed}; aynı paketle onarıldı ({repairExit}) ama {from} sağlıklı açılmadı");
        }
    }
}
