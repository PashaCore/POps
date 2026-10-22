using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using POps.Shared;

#nullable disable

namespace POpsAgent
{
    // POpsAgent.exe --generalize [--enroll-token <jeton>]: disk imajı almadan önce son adım olarak çalıştırılır.
    // Servisi (ve servisi yeniden başlatabilen watchdog'u) durdurur, cihaza özel dosyaları siler; jeton verilirse
    // secure\enroll.token'a yazar. İmajdan açılan her bilgisayar ilk açılışta kimliğini kendi donanımından türetir ve
    // jetonla kendini kaydeder. Çıkış kodları:
    //   0 tamam; 1 bazı dosyalar silinemedi ya da jeton yazılamadı; 2 yönetici değil;
    //   3 servis ya da watchdog durdurulamadı; 4 geçersiz kullanım (bilinmeyen argüman, eksik ya da biçimsiz jeton).
    // 2, 3 ve 4'te hiçbir dosyaya dokunulmaz.
    [SupportedOSPlatform("windows")]
    public static class Generalizer
    {
        public const string Switch = "--generalize";
        public const string TokenSwitch = "--enroll-token";
        public const int ExitOk = 0, ExitIncomplete = 1, ExitNotAdmin = 2, ExitNotStopped = 3, ExitUsage = 4;

        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

        // İşletim sistemi sınırları testlerde sahteleriyle değiştirilir
        internal static Func<bool> IsAdministrator { get; set; } = () =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        // Başarıda null, aksi halde neden
        internal static Func<string> StopAgent { get; set; } = StopServiceAndWatchdog;

        public static bool IsRequested(string[] args) =>
            args != null && args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

        public static bool TryParse(string[] args, out string token, out string error)
        {
            token = null;
            error = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(args[i], TokenSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length) { error = $"{TokenSwitch} bir jeton bekliyor."; return false; }
                    token = args[++i].Trim();
                    if (!AgentCredentials.IsWellFormed(token)) { error = "Jeton biçimi geçersiz (16-256 karakter; harf, rakam, - ve _)."; return false; }
                    continue;
                }
                error = $"Bilinmeyen argüman: {args[i]}";
                return false;
            }
            return true;
        }

        // paths: servisin klasörleri (appsettings.json DataDirectory; bkz. AgentDirectories)
        public static int Run(string[] args, TextWriter output, AgentPaths paths)
        {
            if (!TryParse(args, out string token, out string error))
            {
                output.WriteLine(error);
                output.WriteLine($"Kullanım: POpsAgent.exe {Switch} [{TokenSwitch} <jeton>]");
                return ExitUsage;
            }
            if (!IsAdministrator())
            {
                output.WriteLine("Yönetici olarak çalıştırın (yükseltilmiş komut istemi).");
                return ExitNotAdmin;
            }
            string stopError = StopAgent();
            if (stopError != null)
            {
                output.WriteLine($"POpsAgent durdurulamadı, hiçbir dosyaya dokunulmadı: {stopError}");
                return ExitNotStopped;
            }

            bool complete = true;
            foreach (string path in DeviceFiles(paths))
            {
                if (path == null || !File.Exists(path)) continue;
                try
                {
                    File.Delete(path);
                    output.WriteLine($"Silindi: {path}");
                }
                catch (Exception ex)
                {
                    complete = false;
                    output.WriteLine($"Silinemedi: {path} ({ex.Message})");
                }
            }
            foreach (string folder in CloneFolders(paths))
            {
                try
                {
                    Directory.Delete(folder, true);
                    output.WriteLine($"Silindi: {folder}");
                }
                catch (Exception ex)
                {
                    complete = false;
                    output.WriteLine($"Silinemedi: {folder} ({ex.Message})");
                }
            }

            if (token != null)
            {
                try
                {
                    SecureStore.EnsureDirectory(paths.SecureDir);
                    SecureStore.WriteProtected(paths.SecureFile(AgentCredentials.EnrollTokenFileName), token);
                    output.WriteLine($"Enroll jetonu yazıldı: {paths.SecureFile(AgentCredentials.EnrollTokenFileName)}");
                }
                catch (Exception ex)
                {
                    complete = false;
                    output.WriteLine($"Enroll jetonu yazılamadı: {ex.Message}");
                }
            }

            POpsHelpers.Log("AGENT", $"--generalize: cihaza özel dosyalar silindi{(token != null ? ", enroll jetonu yazıldı" : "")}{(complete ? "" : " (eksik; ayrıntı konsolda)")}.", !complete);
            output.WriteLine(complete
                ? "Tamam. Servis durduruldu; bilgisayarı kapatıp imajı alın. Servis yeniden başlarsa bu bilgisayar yeniden kaydolur; o zaman komutu tekrar çalıştırın."
                : "Bazı adımlar tamamlanamadı (yukarıya bakın).");
            return complete ? ExitOk : ExitIncomplete;
        }

        // Cihaza özel dosyalar: kimlik, secret (ve PersistDir kopyası), cihaz bypass anahtarı, donanım bağı,
        // onay bekleyen görev ve güncelleme sonuçları, son yazılım envanteri gönderimi
        public static IEnumerable<string> DeviceFiles(AgentPaths paths)
        {
            yield return paths.IdentityPath;
            yield return paths.SecureFile(AgentCredentials.SecretFileName);
            yield return AgentCredentials.PersistPath(AgentCredentials.SecretFileName);
            yield return paths.SecureFile(AgentCredentials.DeviceBypassSecretFileName);
            yield return paths.SecureFile(HardwareBinding.FileName);
            yield return AgentCredentials.PersistPath(HardwareBinding.FileName);
            yield return paths.SecureFile(ResultSpool.FileName);
            yield return paths.UpdateResultPath;
            yield return paths.ReportedResultPath;
            yield return paths.DataFile(SoftwareReporter.StateFileName);
        }

        // Kopya algılandığında kenara alınan eski kimlik ve anahtarlar (secure\clone-*)
        private static IEnumerable<string> CloneFolders(AgentPaths paths)
        {
            if (!Directory.Exists(paths.SecureDir)) return Array.Empty<string>();
            return Directory.GetDirectories(paths.SecureDir, HardwareBinding.CloneFolderPrefix + "*");
        }

        // Servis durdurulur; ardından watchdog kapatılır (yönetici oturumundaki watchdog servisi 10 sn içinde yeniden
        // başlatırdı). Arada yeniden başlatıldıysa servis bir kez daha durdurulur. Servis kurulu değilse sorun yok.
        private static string StopServiceAndWatchdog()
        {
            try
            {
                using var service = new ServiceController("POpsAgent");
                try { _ = service.Status; }
                catch (InvalidOperationException) { service.Dispose(); return KillWatchdogs(); }

                StopAndWait(service);
                string watchdog = KillWatchdogs();
                if (watchdog != null) return watchdog;
                StopAndWait(service);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static void StopAndWait(ServiceController service)
        {
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped) return;
            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
        }

        private static string KillWatchdogs()
        {
            foreach (Process process in Process.GetProcessesByName("POpsWatchdog"))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        if (!process.WaitForExit(5000)) return $"POpsWatchdog (PID {process.Id}) kapanmadı.";
                    }
                    catch (InvalidOperationException) { }   // bu arada kendisi kapandı
                    catch (Exception ex) { return $"POpsWatchdog (PID {process.Id}) kapatılamadı: {ex.Message}"; }
                }
            }
            return null;
        }
    }
}
