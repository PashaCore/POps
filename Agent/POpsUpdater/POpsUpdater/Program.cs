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
    //               [--datadir <veri klasörü> --logdir <log klasörü>]
    // Veri ve log klasörlerini servis geçirir (appsettings.json DataDirectory / LogDirectory, bkz.
    // POps.Shared.FolderSettings); updater aynı kurallarla yeniden denetler, verilmezse C:\POpsData ve C:\POpsLogs.
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
        // Servisin geçirdiği klasörler (Main'de, ilk log satırından önce)
        static string DataDir = FolderSettings.DefaultDataDirectory;
        static string LogDir = FolderSettings.DefaultLogDirectory;
        const string ServiceName = "POpsAgent";
        // Installer/agent/Package.wxs UpgradeCode
        const string UpgradeCode = "{1F4A5444-0EA0-40FE-8D23-C5233D4576D1}";

        static string LockPath => Path.Combine(DataDir, "update.lock");
        static string HealthPath => Path.Combine(DataDir, "health.json");
        static string ResultPath => Path.Combine(DataDir, "update-result.json");
        // Kurulumun aşaması servise (o da sunucuya "update_progress" olarak) gider; bkz. POps.Shared.UpdateProgressFile
        static string ProgressPath => Path.Combine(DataDir, UpdateProgressFile.FileName);
        const int MsiexecAttempts = 5;
        // Bu çalışma: update.lock'taki started_at ve hedef sürüm (aşama dosyasına yazılır)
        static long _run;
        static string _toVersion;
        // MSI her kurulumda kendi paketini buraya installed.msi olarak bırakır (geri dönüş kaynağı)
        static string PackagesDir => Path.Combine(DataDir, "packages");
        static string BackupRoot => Path.Combine(DataDir, "backup");
        static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);
        // Geri dönüş tatbikatı: yönetici bu dosyayı koyarsa yeni sürüm health.json yazmaz (bkz. Agent/README.md)
        // Tatbikat işaretleri (rollback-drill ve yeni ajanın tükettiği rollback-drill.consumed; bkz. POps.Shared.RollbackDrill)
        static string SecureDir => Path.Combine(DataDir, "secure");

        // msiexec'in kurulum hiç başlamadan döndüğü kodlar: bu durumlarda makinede hiçbir şey değişmez
        static readonly Dictionary<int, string> InstallNeverStarted = new Dictionary<int, string>
        {
            [1602] = "kullanıcı iptal etti",
            [1618] = "başka bir Windows Installer kurulumu sürüyor",
            [1619] = "paket açılamadı",
            [1620] = "paket geçersiz",
            [1622] = "kurulum logu açılamadı",
            [1625] = "sistem politikası kurulumu engelliyor",
            [1633] = "platform desteklenmiyor",
            [1638] = "ürünün başka bir sürümü kurulu",
            [1639] = "komut satırı geçersiz",
        };
        // "POpsVision": 0.1.2 öncesi kurulumlarda ayrı ekran yakalama süreci vardı (kaynak 0.1.14'te kaldırıldı); o
        // sürümlerden yükseltirken hâlâ çalışıyor olabilir ve dosyaları kilitler, bu yüzden adı listede kalır.
        static readonly string[] UserProcesses = { "POpsWatchdog", "POpsTray", "POpsVision" };

        sealed class Options
        {
            public string Msi, Sha256, From, To, InstallDir;
        }

        static int Main(string[] args)
        {
            // SYSTEM olarak çalışır; ajanla aynı log dosyasına (C:\POpsLogs\POps_<tarih>.log ya da LogDirectory) yazar
            POpsHelpers.Component = "Updater";
            // Servisin kullandığı veri ve log klasörleri, servisle aynı kurallarla (kurulum klasörü dahil); geçersizse varsayılan
            FolderSettings folders = FolderSettings.FromArguments(args, FolderRules.ForMachine(FolderSettings.ArgumentValue(args, "--installdir")), checkLocation: true);
            DataDir = folders.DataDirectory;
            LogDir = folders.LogDirectory;
            POpsHelpers.MachineLogDir = LogDir;
            foreach (string problem in folders.Problems) Log($"[HATA] {problem}", true);
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
                _toVersion = opt.To;
                _run = LockStartedAt(opt);
                UpdateProgressFile.Delete(ProgressPath);

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
                int exit = RunMsiexec($"/i \"{opt.Msi}\" /qn /norestart REBOOT=ReallySuppress{installFolderArg}", "install-" + opt.To, reportProgress: true);
                result["msi_exit_code"] = exit;
                result["reboot_required"] = exit == 3010;

                if (exit != 0 && exit != 3010)
                {
                    if (previousMsi != null) CopyPackage(previousMsi, Path.Combine(PackagesDir, "installed.msi"));
                    if (InstallNeverStarted.TryGetValue(exit, out string reason))
                    {
                        // Kurulum hiç başlamadı: makinede hiçbir şey değişmedi
                        outcome = "install_failed";
                        result["detail"] = $"msiexec {exit}: {reason}; kurulum başlamadı, kurulu sürüm ({opt.From}) değişmedi";
                    }
                    else
                    {
                        // Kurulum işlem içinde başarısız oldu; eski ürün aynı işlem içinde kaldırıldığı için
                        // Windows Installer onu geri yükledi: güncelleme uygulanmadı ama makine önceki sürümde
                        outcome = "rolled_back";
                        result["rollback"] = "msi_transaction";
                        result["detail"] = $"msiexec {exit}: kurulum başarısız, Windows Installer kurulu sürüme ({opt.From}) geri döndü";
                    }
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
                    // Tatbikat işareti geri kurulumdan ÖNCE silinir: geri kurulan sürüm (işareti tanısın tanımasın) onu
                    // görüp sağlık bildirmezse geri dönüş sahte bir "rollback_failed" olurdu
                    ClearRollbackDrill("geri kurulumdan önce");
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
                result["running_version"] = RunningVersion();
                result["outcome"] = outcome;
                // Tatbikat işareti tek seferliktir: bir sonraki güncelleme normal ilerler
                ClearRollbackDrill("güncelleme sonunda");
                result["finished_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // Aşama dosyası sonuçtan önce gider: servis sonucu gördüğünde eski aşamayı iletmez
                UpdateProgressFile.Delete(ProgressPath);
                WriteAtomic(ResultPath, JsonSerializer.Serialize(result));
                Log($"Güncelleme bitti: {outcome} ({JsonSerializer.Serialize(result)})", outcome != "success");
                try { File.Delete(LockPath); } catch { }
                LaunchUserApps();
            }
        }

        static void ClearRollbackDrill(string when)
        {
            if (RollbackDrill.Clear(SecureDir, (message, error) => Log(message, error)) > 0)
                Log($"[TATBİKAT] rollback-drill işaretleri silindi ({when}).");
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
                // msiexec 0 dönse de servis ve exe hemen denetlenir, eksikse aynı paketle onarılır (bkz. UpdaterRollback).
                var steps = new UpdaterRollback.Steps
                {
                    RunMsiexec = RunMsiexec,
                    ServiceExists = ServiceExists,
                    AgentExeVersion = () => AgentExeVersion(opt),
                    PackageProductInstalled = () => MsiPackage.TryRead(previousMsi, out _) is MsiPackage package && MsiPackage.IsInstalled(package.ProductCode),
                    EnsureServiceRunning = EnsureServiceRunning,
                    WaitForHealth = () => WaitForHealth(opt.From, start),
                    Sleep = Thread.Sleep,
                    Log = (message, error) => Log(message, error),
                };
                return UpdaterRollback.RunMsi(steps, previousMsi, installFolderArg, opt.From, opt.To);
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

        // Geri dönüş ve son çare onarımı aşama bildirmez
        static int RunMsiexec(string arguments, string logName) => RunMsiexec(arguments, logName, reportProgress: false);

        // Başka bir kurulum sürüyorsa (1618) bir süre beklenip yeniden denenir. reportProgress: ana kurulum; her
        // denemeden önce "installing", 1618 beklemesinde "waiting_installer" yazılır.
        static int RunMsiexec(string arguments, string logName, bool reportProgress)
        {
            // msiexec log klasörünü oluşturmaz; yoksa kurulum 1622 ile düşer
            Directory.CreateDirectory(LogDir);
            string log = Path.Combine(LogDir, $"msi-{Safe(logName)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            for (int attempt = 1; ; attempt++)
            {
                TouchLock();
                if (reportProgress) WriteProgress(UpdateProgressFile.Installing, attempt, null);
                Log($"msiexec {arguments} (log: {log})");
                using Process p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"{arguments} /l*v \"{log}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                p.WaitForExit();
                Log($"msiexec çıkış kodu: {p.ExitCode}");
                if (p.ExitCode != 1618 || attempt == MsiexecAttempts) return p.ExitCode;
                Log("Başka bir Windows Installer işlemi sürüyor (1618); 60 sn sonra yeniden denenecek.");
                if (reportProgress) WriteProgress(UpdateProgressFile.WaitingInstaller, attempt, "msiexec 1618: başka bir Windows Installer kurulumu sürüyor");
                Thread.Sleep(TimeSpan.FromSeconds(60));
            }
        }

        // Yeni sürüm çekirdek başlangıcı tamamlanınca phase=operational health.json yazar. Geri dönüşteki eski
        // sürümlerin phase alanı yoktur; HealthCheck bu biçimi version + ts ile geriye uyumlu kabul eder.
        static bool WaitForHealth(string expectedVersion, DateTime notBeforeUtc)
        {
            DateTime deadline = DateTime.UtcNow + HealthTimeout;
            string expected = expectedVersion.TrimStart('v');
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(HealthPath))
                    {
                        string json = File.ReadAllText(HealthPath);
                        if (HealthCheck.IsHealthy(json, expected, notBeforeUtc))
                        {
                            Log($"health.json doğrulandı: {expected}");
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

        // İşlem sonunda makinede çalışan ajanın sürümü (en son açılan servisin yazdığı health.json); bilinmiyorsa null
        static string RunningVersion()
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(HealthPath));
                return doc.RootElement.TryGetProperty("version", out JsonElement v) ? v.GetString() : null;
            }
            catch { return null; }
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
                RunMsiexec(UpdaterRollback.RepairArguments(package, info != null && MsiPackage.IsInstalled(info.ProductCode), ""), "last-resort");
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

        // Servisin çalıştırdığı (servis yoksa kurulum klasöründeki) POpsAgent.exe'nin FileVersion'ı; dosya yoksa null
        static string AgentExeVersion(Options opt)
        {
            try
            {
                string exe = Path.Combine(ServiceInstallDir() ?? opt.InstallDir, "POpsAgent.exe");
                return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).FileVersion : null;
            }
            catch (Exception ex)
            {
                Log($"POpsAgent.exe sürümü okunamadı: {ex.Message}", true);
                return null;
            }
        }

        // Servisin gerçekte çalıştırdığı exe'nin klasörü (MSI'sız kurulumdan MSI'a geçişte değişir)
        static string ServiceInstallDir()
        {
            try
            {
                string image = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{ServiceName}", "ImagePath", null) as string;
                // Tırnaksız ve boşluklu yol da (C:\Program Files\POps\POpsAgent.exe) doğru okunur
                string exe = ServiceImagePath.ExecutablePath(image);
                return exe == null ? null : Path.GetDirectoryName(exe);
            }
            catch { return null; }
        }

        // Güncelleme (ya da geri dönüş) bitince watchdog ve tepsi kullanıcı oturumunda başlatılır; oturum kapatıp açmak
        // gerekmez. Eskiden "schtasks /ru BUILTIN\Users /it" kullanılıyordu ve sahada tepsiyi başlatmıyordu
        // (bkz. POps.Shared.UserSessionLauncher). Kurulu sürüm hangisiyse onun klasöründen.
        static void LaunchUserApps()
        {
            try
            {
                uint session = UserSessionLauncher.ActiveConsoleSession();
                if (!UserSessionLauncher.HasSignedInUser(session))
                {
                    Log("Oturum açmış kullanıcı yok; tepsi ve watchdog oturum açılınca başlayacak.");
                    return;
                }
                string dir = ServiceInstallDir();
                if (dir == null) return;
                var (watchdog, tray) = UserAppsPolicy.WhatToStart(true, UserSessionLauncher.WindowsInstallerBusy(),
                    UserSessionLauncher.IsRunning(Path.Combine(dir, "POpsWatchdog.exe")), UserSessionLauncher.IsRunning(Path.Combine(dir, "POpsTray.exe")),
                    shellReady: true, signedInFor: TimeSpan.MaxValue);
                foreach ((bool start, string exe) in new[] { (watchdog, "POpsWatchdog.exe"), (tray, "POpsTray.exe") })
                {
                    if (!start) continue;
                    // Watchdog update.lock'u servisin veri klasöründe arar (servis de aynı argümanla başlatır)
                    string arguments = exe == "POpsWatchdog.exe" ? FolderSettings.Argument(FolderSettings.DataDirectorySwitch, DataDir) : null;
                    if (UserSessionLauncher.TryStart(session, Path.Combine(dir, exe), out int pid, out string error, arguments))
                        Log($"{exe} kullanıcı oturumunda başlatıldı (oturum {session}, PID {pid}).");
                    else
                        Log($"{exe} kullanıcı oturumunda başlatılamadı: {error}", true);
                }
            }
            catch (Exception ex) { Log($"Tepsi/watchdog başlatılamadı: {ex.Message}", true); }
        }

        // ------------------------------------------------------------------------------------------
        static void WriteProgress(string stage, int attempt, string detail)
        {
            try
            {
                UpdateProgressFile.Write(ProgressPath, new UpdateProgressRecord
                {
                    Run = _run,
                    ToVersion = _toVersion,
                    Stage = stage,
                    Attempt = attempt,
                    Of = MsiexecAttempts,
                    Detail = detail,
                    At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
            }
            catch (Exception ex) { Log($"{ProgressPath} yazılamadı: {ex.Message}", true); }
        }

        // update.lock'taki started_at (servis yazar). Yoksa updater'ın kendi başlangıç anı kilide yazılır.
        static long LockStartedAt(Options opt)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(LockPath));
                if (doc.RootElement.TryGetProperty("started_at", out JsonElement started) && started.TryGetInt64(out long value) && value > 0)
                    return value;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException || ex is InvalidOperationException) { }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteAtomic(LockPath, JsonSerializer.Serialize(new { from_version = opt.From, to_version = opt.To, started_at = now }));
            return now;
        }

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
