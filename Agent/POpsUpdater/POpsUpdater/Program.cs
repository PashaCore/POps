using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

#nullable disable

namespace POpsUpdater
{
    // Ajan güncellemesini MSI ile uygular (Faz 7). Ajan (SYSTEM) imzalı manifest'i ve paketi doğruladıktan
    // sonra bu programı kurulum klasörü dışından (C:\POpsData\updater) başlatır:
    //   POpsUpdater --msi <paket> --sha256 <özet> --from <kurulu sürüm> --to <yeni sürüm> --installdir <klasör>
    // Adımlar: kilit, özet kontrolü, geri dönüş paketinin doğrulanması, dosya yedeği, kullanıcı süreçlerini
    // kapatma, msiexec /i, yeni sürümün health.json'unu bekleme; yeni sürüm sağlıklı açılmazsa önceki MSI'a
    // (yoksa dosya yedeğine) dönüş; her durumda ajan servisinin varlık/çalışma kontrolü; update-result.json;
    // kilidi kaldırma. update.lock varken watchdog servisi ve tepsiyi yeniden başlatmaz.
    //
    // Hiçbir adım çalışan kurulumu, yerine geleceği kanıtlanmadan kaldırmaz: yükseltme ve geri dönüş tek bir
    // Windows Installer işlemidir (başarısızsa kurulu sürüm yerinde kalır); ayrı bir "msiexec /x" yoktur.
    [SupportedOSPlatform("windows")]
    static class Program
    {
        const string DataDir = @"C:\POpsData";
        const string LogDir = @"C:\POpsLogs";
        const string ServiceName = "POpsAgent";
        // Installer/agent/Package.wxs UpgradeCode
        const string UpgradeCode = "{1F4A5444-0EA0-40FE-8D23-C5233D4576D1}";

        static readonly string LockPath = Path.Combine(DataDir, "update.lock");
        static readonly string HealthPath = Path.Combine(DataDir, "health.json");
        static readonly string ResultPath = Path.Combine(DataDir, "update-result.json");
        // MSI her kurulumda kendi paketini buraya installed.msi olarak bırakır (geri dönüş kaynağı)
        static readonly string PackagesDir = Path.Combine(DataDir, "packages");
        static readonly string BackupRoot = Path.Combine(DataDir, "backup");
        static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);
        static readonly string[] UserProcesses = { "POpsWatchdog", "POpsTray", "POpsVision" };

        sealed class Options
        {
            public string Msi, Sha256, From, To, InstallDir;
        }

        static int Main(string[] args)
        {
            Options opt = ParseArgs(args);
            if (opt == null)
            {
                Log("Kullanım: POpsUpdater --msi <paket> --sha256 <özet> --from <sürüm> --to <sürüm> --installdir <klasör>", true);
                return 2;
            }

            var result = new Dictionary<string, object>
            {
                ["schema"] = "pops-update-result/1",
                ["from_version"] = opt.From,
                ["to_version"] = opt.To,
                ["started_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["rollback"] = "none",
            };
            string outcome = "error";
            try
            {
                Log($"Güncelleme başladı: {opt.From} -> {opt.To} ({opt.Msi})");
                TouchLock();

                if (!HashMatches(opt.Msi, opt.Sha256))
                {
                    outcome = "rejected";
                    result["detail"] = "paketin SHA-256'sı ajanın doğruladığı özetle uyuşmuyor";
                    return 1;
                }

                string previousMsi = PrepareRollbackPackage();
                BackupInstall(opt.InstallDir, opt.From);
                StopUserProcesses();

                // MSI ile kurulu bir sürüm varsa aynı klasöre kurulur; MSI'sız eski kurulumda varsayılan klasör kullanılır
                string installFolderArg = IsMsiManaged() ? $" INSTALLFOLDER=\"{opt.InstallDir.TrimEnd('\\')}\"" : "";

                DateTime installStart = DateTime.UtcNow;
                int exit = RunMsiexec($"/i \"{opt.Msi}\" /qn /norestart REBOOT=ReallySuppress{installFolderArg}", "install-" + opt.To);
                result["msi_exit_code"] = exit;
                result["reboot_required"] = exit == 3010;

                if (exit != 0 && exit != 3010)
                {
                    // Eski ürün aynı işlem içinde kaldırıldığı için Windows Installer onu geri yükledi
                    outcome = "install_failed";
                    result["rollback"] = "msi_transaction";
                    result["detail"] = $"msiexec {exit} döndü; Windows Installer değişiklikleri geri aldı";
                    if (previousMsi != null) CopyPackage(previousMsi, Path.Combine(PackagesDir, "installed.msi"));
                }
                else if (WaitForHealth(opt.To, installStart))
                {
                    outcome = "success";
                }
                else if (exit == 3010)
                {
                    // Kullanımdaki dosyalar yeniden başlatmada değişecek; yeni sürüm ancak o zaman açılır.
                    // Burada geri dönmek, tamamlanmak üzere olan sağlam bir kurulumu bozardı.
                    outcome = "pending_reboot";
                    result["detail"] = "msiexec 3010: kurulum yeniden başlatmada tamamlanacak; geri dönülmedi";
                }
                else
                {
                    Log($"Yeni sürüm {HealthTimeout.TotalSeconds:0} sn içinde sağlıklı açılmadı; geri dönülüyor.", true);
                    (outcome, string rollback, string detail) = Rollback(opt, previousMsi, installFolderArg);
                    result["rollback"] = rollback;
                    result["detail"] = detail;
                }
                return outcome == "success" ? 0 : 1;
            }
            catch (Exception ex)
            {
                result["detail"] = ex.Message;
                Log($"Güncelleme hatası: {ex}", true);
                return 1;
            }
            finally
            {
                result["agent_state"] = EnsureAgentPresent(opt);
                result["outcome"] = outcome;
                result["finished_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                WriteAtomic(ResultPath, JsonSerializer.Serialize(result));
                Log($"Güncelleme bitti: {outcome} ({JsonSerializer.Serialize(result)})", outcome != "success");
                try { File.Delete(LockPath); } catch { }
                LaunchWatchdog();
            }
        }

        // ------------------------------------------------------------------------------------------
        // Geri dönüş
        // ------------------------------------------------------------------------------------------
        static (string Outcome, string Rollback, string Detail) Rollback(Options opt, string previousMsi, string installFolderArg)
        {
            DateTime start = DateTime.UtcNow;
            if (previousMsi != null)
            {
                // Tek işlem: önceki paket, kurulu yeni sürümü aynı Windows Installer işlemi içinde kaldırıp kendini
                // kurar (POPS_ROLLBACK=1 sürüm düşürme engelini yalnızca bu çağrı için açar). İşlem başarısız
                // olursa Windows Installer yeni sürümü yerinde bırakır; makine hiçbir anda ajansız kalmaz.
                int exit = -1;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    exit = RunMsiexec($"/i \"{previousMsi}\" /qn /norestart REBOOT=ReallySuppress POPS_ROLLBACK=1{installFolderArg}", $"rollback-{opt.From}-{attempt}");
                    if (exit == 0 || exit == 3010) break;
                    if (attempt == 1)
                    {
                        Log($"Geri kurulum {exit} döndü; 30 sn sonra bir kez daha denenecek.", true);
                        Thread.Sleep(TimeSpan.FromSeconds(30));
                    }
                }
                if (exit == 3010)
                    return ("rollback_pending_reboot", "msi", $"{opt.To} sağlıklı açılmadı; {opt.From} kuruldu, yeniden başlatmada tamamlanacak");
                if (exit == 0 && WaitForHealth(opt.From, start))
                    return ("rolled_back", "msi", $"{opt.To} sağlıklı açılmadı; {opt.From} geri kuruldu");
                if (exit == 0)
                    return ("rollback_failed", "msi", $"{opt.To} sağlıklı açılmadı; {opt.From} geri kuruldu ama o da sağlıklı açılmadı");
                return ("rollback_failed", "msi", $"{opt.To} sağlıklı açılmadı; geri kurulum {exit} döndü ve Windows Installer {opt.To} sürümünü yerinde bıraktı");
            }

            // Önceki MSI yok (ilk MSI'dan önceki kurulum): dosya yedeği geri yüklenir. Windows Installer kaydı
            // yeni sürümde kalır; bir sonraki başarılı güncelleme bunu düzeltir.
            if (RestoreBackup(opt) && WaitForHealth(opt.From, start))
                return ("rolled_back", "files", $"{opt.To} sağlıklı açılmadı; {opt.From} dosya yedeğinden geri yüklendi");
            return ("rollback_failed", "files", $"{opt.To} sağlıklı açılmadı; dosya yedeği geri yüklenemedi");
        }

        static bool RestoreBackup(Options opt)
        {
            string backup = Path.Combine(BackupRoot, Safe(opt.From));
            string target = ServiceInstallDir() ?? opt.InstallDir;
            if (!Directory.Exists(backup)) return false;
            try
            {
                StopService();
                foreach (string file in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
                {
                    string dest = Path.Combine(target, Path.GetRelativePath(backup, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest, true);
                }
                EnsureServiceRunning();
                return true;
            }
            catch (Exception ex)
            {
                Log($"Dosya yedeği geri yüklenemedi: {ex.Message}", true);
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Adımlar
        // ------------------------------------------------------------------------------------------
        static bool HashMatches(string path, string expectedHex)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                byte[] actual = SHA256.HashData(stream);
                return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHex));
            }
            catch (Exception ex)
            {
                Log($"Paket özeti okunamadı: {ex.Message}", true);
                return false;
            }
        }

        // Kurulum yeni paketi installed.msi olarak bırakacağı için şu anki kurulu paket önce previous.msi olarak
        // saklanır. Geri dönüş kaynağı sayılması için: kopya kaynağıyla aynı özette olmalı, POps Agent paketi
        // olmalı, POPS_ROLLBACK'i desteklemeli ve bu makinede şu an kurulu olan ürünün paketi olmalı.
        static string PrepareRollbackPackage()
        {
            string installed = Path.Combine(PackagesDir, "installed.msi");
            if (!File.Exists(installed))
            {
                Log("Kurulu sürümün MSI paketi yok; geri dönüş gerekirse dosya yedeği kullanılacak.");
                return null;
            }
            string previous = Path.Combine(PackagesDir, "previous.msi");
            try
            {
                File.Copy(installed, previous, true);
                if (!CryptographicOperations.FixedTimeEquals(Sha256Of(installed), Sha256Of(previous)))
                    return RollbackPackageRejected("kopya kaynağıyla aynı değil");

                MsiPackage package = MsiPackage.TryRead(previous, out string error);
                if (package == null) return RollbackPackageRejected(error);
                if (!string.Equals(package.UpgradeCode, UpgradeCode, StringComparison.OrdinalIgnoreCase))
                    return RollbackPackageRejected($"başka bir ürünün paketi ({package.UpgradeCode})");
                if (!package.SupportsRollback)
                    return RollbackPackageRejected("paket POPS_ROLLBACK ile geri kurulumu desteklemiyor");
                if (!MsiPackage.IsInstalled(package.ProductCode))
                    return RollbackPackageRejected($"paket ({package.ProductVersion}) şu an kurulu ürünün paketi değil");

                Log($"Geri dönüş paketi doğrulandı: {package.ProductVersion} ({package.ProductCode}).");
                return previous;
            }
            catch (Exception ex)
            {
                return RollbackPackageRejected(ex.Message);
            }
        }

        static string RollbackPackageRejected(string reason)
        {
            Log($"Önceki MSI geri dönüş için kullanılmayacak: {reason}. Geri dönüş gerekirse dosya yedeği kullanılacak.", true);
            return null;
        }

        static byte[] Sha256Of(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return SHA256.HashData(stream);
        }

        static void CopyPackage(string from, string to)
        {
            try { File.Copy(from, to, true); }
            catch (Exception ex) { Log($"{from} kopyalanamadı: {ex.Message}", true); }
        }

        // Kurulum klasörünün dosya dosya yedeği (ayar dosyası hariç; o güncellemede değişmez)
        static void BackupInstall(string installDir, string version)
        {
            try
            {
                if (Directory.Exists(BackupRoot)) Directory.Delete(BackupRoot, true);
                string backup = Path.Combine(BackupRoot, Safe(version));
                foreach (string file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)) continue;
                    string dest = Path.Combine(backup, Path.GetRelativePath(installDir, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest, true);
                }
                Log($"Kurulum klasörü yedeklendi: {backup}");
            }
            catch (Exception ex)
            {
                Log($"Yedek alınamadı (güncelleme sürüyor): {ex.Message}", true);
            }
        }

        // Kullanıcı oturumundaki süreçler dosyaları kilitler; eski watchdog kilit dosyasını tanımadığı için kapatılır
        static void StopUserProcesses()
        {
            foreach (string name in UserProcesses)
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                        Log($"{name} kapatıldı (PID {p.Id}, oturum {p.SessionId}).");
                    }
                    catch (Exception ex) { Log($"{name} kapatılamadı: {ex.Message}", true); }
                    finally { p.Dispose(); }
                }
        }

        // Başka bir kurulum sürüyorsa (1618) bir süre beklenip yeniden denenir
        static int RunMsiexec(string arguments, string logName)
        {
            // msiexec log klasörünü oluşturmaz; yoksa kurulum 1622 ile düşer
            Directory.CreateDirectory(LogDir);
            string log = Path.Combine(LogDir, $"msi-{Safe(logName)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            for (int attempt = 1; ; attempt++)
            {
                TouchLock();
                Log($"msiexec {arguments} (log: {log})");
                using Process p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"{arguments} /l*v \"{log}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                p.WaitForExit();
                Log($"msiexec çıkış kodu: {p.ExitCode}");
                if (p.ExitCode != 1618 || attempt == 5) return p.ExitCode;
                Log("Başka bir Windows Installer işlemi sürüyor (1618); 60 sn sonra yeniden denenecek.");
                Thread.Sleep(TimeSpan.FromSeconds(60));
            }
        }

        // Yeni sürüm açılışta health.json yazar. Güncelleme öncesinden kalan dosya, yazılma zamanıyla ayırt edilir.
        static bool WaitForHealth(string expectedVersion, DateTime notBeforeUtc)
        {
            DateTime deadline = DateTime.UtcNow + HealthTimeout;
            string expected = expectedVersion.TrimStart('v');
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var file = new FileInfo(HealthPath);
                    if (file.Exists && file.LastWriteTimeUtc >= notBeforeUtc)
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(HealthPath));
                        string version = doc.RootElement.TryGetProperty("version", out JsonElement v) ? v.GetString()?.TrimStart('v') : null;
                        if (string.Equals(version, expected, StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"health.json doğrulandı: {version}");
                            return true;
                        }
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
                TouchLock();
                Thread.Sleep(2000);
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------
        // Servis, MSI kaydı, watchdog
        // ------------------------------------------------------------------------------------------
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint productIndex, StringBuilder productCode);

        static bool IsMsiManaged()
        {
            var productCode = new StringBuilder(39);
            return MsiEnumRelatedProducts(UpgradeCode, 0, 0, productCode) == 0;
        }

        // Her sonuçtan sonra: POpsAgent servisi var ve çalışıyor olmalı. Servis yoksa son çare olarak elde kalan
        // paketle onarım/kurulum yapılır; o da olmazsa durum [KRİTİK] olarak loglanır ve sonuca yazılır.
        static string EnsureAgentPresent(Options opt)
        {
            try
            {
                if (ServiceExists())
                {
                    EnsureServiceRunning();
                    return ServiceRunning() ? "running" : "not_running";
                }

                Log("[KRİTİK] POpsAgent servisi yok; son çare kurulum deneniyor.", true);
                string package = new[] { Path.Combine(PackagesDir, "installed.msi"), opt.Msi }.FirstOrDefault(File.Exists);
                if (package == null)
                {
                    Log("[KRİTİK] Kurulabilecek paket yok; cihaz yönetimsiz kaldı, elle kurulum gerekiyor.", true);
                    return "unmanaged";
                }
                MsiPackage info = MsiPackage.TryRead(package, out _);
                string arguments = info != null && MsiPackage.IsInstalled(info.ProductCode)
                    ? $"/fvamus \"{package}\" /qn /norestart REBOOT=ReallySuppress"
                    : $"/i \"{package}\" /qn /norestart REBOOT=ReallySuppress POPS_ROLLBACK=1";
                RunMsiexec(arguments, "last-resort");
                EnsureServiceRunning();
                if (ServiceExists() && ServiceRunning())
                {
                    Log("[KRİTİK] Ajan son çare kurulumla geri getirildi.", true);
                    return "reinstalled";
                }
                Log("[KRİTİK] Son çare kurulum da ajanı çalıştıramadı; cihaz yönetimsiz kaldı, elle kurulum gerekiyor.", true);
                return "unmanaged";
            }
            catch (Exception ex)
            {
                Log($"[KRİTİK] Ajan durumu doğrulanamadı: {ex.Message}", true);
                return "unknown";
            }
        }

        static bool ServiceExists() =>
            ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));

        static bool ServiceRunning()
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }

        static void StopService()
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped) return;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }

        static void EnsureServiceRunning()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status == ServiceControllerStatus.Running) return;
                if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex) { Log($"{ServiceName} başlatılamadı: {ex.Message}", true); }
        }

        // Servisin gerçekte çalıştırdığı exe'nin klasörü (MSI'sız kurulumdan MSI'a geçişte değişir)
        static string ServiceInstallDir()
        {
            try
            {
                string image = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{ServiceName}", "ImagePath", null) as string;
                if (string.IsNullOrWhiteSpace(image)) return null;
                image = image.Trim();
                string exe = image.StartsWith("\"") ? image.Substring(1, image.IndexOf('"', 1) - 1) : image.Split(' ')[0];
                return Path.GetDirectoryName(exe);
            }
            catch { return null; }
        }

        // Kilit kalktıktan sonra watchdog tepsiyi açar; çalışmıyorsa kullanıcı oturumunda başlatılır
        static void LaunchWatchdog()
        {
            try
            {
                if (Process.GetProcessesByName("POpsWatchdog").Length > 0) return;
                string dir = ServiceInstallDir();
                string exe = dir == null ? null : Path.Combine(dir, "POpsWatchdog.exe");
                if (exe == null || !File.Exists(exe)) return;
                string task = "POpsWatchdogLauncher";
                RunHidden("schtasks.exe", $"/create /tn \"{task}\" /tr \"\\\"{exe}\\\"\" /sc once /st 00:00 /ru \"BUILTIN\\Users\" /it /f");
                RunHidden("schtasks.exe", $"/run /tn \"{task}\"");
                RunHidden("schtasks.exe", $"/delete /tn \"{task}\" /f");
            }
            catch (Exception ex) { Log($"Watchdog başlatılamadı: {ex.Message}", true); }
        }

        static void RunHidden(string file, string arguments)
        {
            using Process p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true });
            p.WaitForExit(15000);
        }

        // ------------------------------------------------------------------------------------------
        static void TouchLock()
        {
            try
            {
                if (File.Exists(LockPath)) File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow);
                else WriteAtomic(LockPath, JsonSerializer.Serialize(new { started_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
            }
            catch { }
        }

        static void WriteAtomic(string path, string content)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, true);
            }
            catch (Exception ex) { Log($"{path} yazılamadı: {ex.Message}", true); }
        }

        static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_'));

        static void Log(string message, bool isError = false) => POpsHelpers.Log("UPDATER", message, isError);

        static Options ParseArgs(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < args.Length; i += 2) map[args[i]] = args[i + 1];
            var opt = new Options
            {
                Msi = map.GetValueOrDefault("--msi"),
                Sha256 = map.GetValueOrDefault("--sha256"),
                From = map.GetValueOrDefault("--from"),
                To = map.GetValueOrDefault("--to"),
                InstallDir = map.GetValueOrDefault("--installdir"),
            };
            bool ok = new[] { opt.Msi, opt.Sha256, opt.From, opt.To, opt.InstallDir }.All(v => !string.IsNullOrWhiteSpace(v))
                      && File.Exists(opt.Msi) && opt.Sha256.Length == 64;
            return ok ? opt : null;
        }
    }
}
