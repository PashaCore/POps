using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace POpsWatchDog
{
    // Kullanıcı oturumunda çalışır; POpsAgent servisini ve tepsiyi (POpsTray) izler, durmuşsa yeniden başlatır.
    // Sunucuya bağlanmaz. Ekran yakalama tepsidedir; ayrı bir POpsVision süreci yoktur (bkz. docs/decisions.md D-13).
    class Program
    {
        // Sürüm kök VERSION dosyasından gelir (Directory.Build.props -> assembly). Elle güncellenmez.
        public static readonly string APP_VERSION = POpsHelpers.AppVersion;

        // Konsol uygulaması olarak başlatılırsa açılan pencere bırakılır (Windows Terminal'de boş pencere kalmasın)
        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();

        static readonly string AgentServiceName = "POpsAgent";
        static readonly string TrayExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "POpsTray.exe");

        // POpsUpdater güncelleme boyunca bu dosyayı tutar; msiexec servisi durdurup tepsiyi kapattığında
        // watchdog onları yeniden başlatıp kurulumla yarışmasın. Updater çökse bile 15 dk sonra yok sayılır.
        static readonly string UpdateLockPath = @"C:\POpsData\update.lock";
        static readonly TimeSpan StaleUpdateLockAge = TimeSpan.FromMinutes(15);
        static bool _updatePauseLogged;

        // Döngü 10 sn'de bir döner; aynı hata (ör. yönetici olmayan kullanıcıda servis başlatılamıyor) en fazla 10 dk'da bir loglanır
        static readonly POps.Shared.LogThrottle ErrorLog = new POps.Shared.LogThrottle(TimeSpan.FromMinutes(10));

        static void Main(string[] args)
        {
            // Kullanıcı oturumunda çalışır: logu %LOCALAPPDATA%\POps\Logs\POpsWatchdog_<tarih>.log
            POpsHelpers.Component = "Watchdog";
            FreeConsole();

            if (args != null && args.Contains("POpsV", StringComparer.OrdinalIgnoreCase))
            {
                SpawnVersionWindow();
                return;
            }

            // Form yok: süreç arka planda döngüyü çalıştırarak yaşar
            Task.Run(() => RunAsync()).GetAwaiter().GetResult();
        }

        static async Task RunAsync()
        {
            POpsHelpers.Log("WATCHDOG", $"POpsWatchdog başladı ({APP_VERSION}): POpsAgent servisi ve tepsi (POpsTray) izleniyor.");
            await PatrolLoopAsync();
        }

        // Denetim döngüsü: 10 sn'de bir tepsi ve servis
        private static async Task PatrolLoopAsync()
        {
            while (true)
            {
                try
                {
                    if (UpdateInProgress())
                    {
                        if (!_updatePauseLogged) POpsHelpers.Log("WATCHDOG", "Güncelleme sürüyor (update.lock); servis ve tepsi yeniden başlatılmıyor.");
                        _updatePauseLogged = true;
                    }
                    else
                    {
                        _updatePauseLogged = false;
                        CheckAndRepairTray();
                        CheckAndRepairAgentService();
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Denetim hatası: {ex.Message}");
                }

                await Task.Delay(10000);
            }
        }

        private static void LogError(string message)
        {
            if (ErrorLog.ShouldLog(message, DateTime.UtcNow)) POpsHelpers.Log("WATCHDOG", message, true);
        }

        private static bool UpdateInProgress()
        {
            try
            {
                var lockFile = new FileInfo(UpdateLockPath);
                return lockFile.Exists && DateTime.UtcNow - lockFile.LastWriteTimeUtc < StaleUpdateLockAge;
            }
            catch { return false; }
        }

        private static void CheckAndRepairTray()
        {
            try
            {
                // Yalnızca ada bakılmaz: POpsTray.exe adını taşıyan başka bir program tepsinin yerini tutamasın
                if (POps.Shared.UserSessionLauncher.IsRunning(TrayExePath)) return;
                if (!File.Exists(TrayExePath))
                {
                    LogError($"{TrayExePath} bulunamadı; tepsi başlatılamadı.");
                    return;
                }
                POpsHelpers.Log("WATCHDOG", "Tepsi (POpsTray) çalışmıyor; başlatılıyor.");
                Process.Start(new ProcessStartInfo
                {
                    FileName = TrayExePath,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception ex)
            {
                LogError($"Tepsi başlatılamadı: {ex.Message}");
            }
        }

        private static void CheckAndRepairAgentService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(AgentServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending)
                    {
                        POpsHelpers.Log("WATCHDOG", "POpsAgent servisi çalışmıyor; başlatılıyor.");
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    }
                }
            }
            catch (Exception ex)
            {
                // Watchdog kullanıcı hesabıyla çalışır: yönetici değilse servisi başlatamaz; servis yoksa da buraya düşer
                LogError($"POpsAgent servisi denetlenemedi ya da başlatılamadı: {ex.Message}");
            }
        }

        static void SpawnVersionWindow()
        {
            try
            {
                string cmd = $"$Host.UI.RawUI.WindowTitle = 'POpsWatchdog'; " +
                             $"Write-Host '========================================' -ForegroundColor Cyan; " +
                             $"Write-Host ' POpsWatchdog - Versiyon: {APP_VERSION}' -ForegroundColor Green; " +
                             $"Write-Host '========================================' -ForegroundColor Cyan; " +
                             $"Write-Host 'Görev: POpsAgent servisini ve tepsiyi (POpsTray) izler, durmuşsa yeniden başlatır.' -ForegroundColor Gray; " +
                             $"Read-Host 'Kapatmak için Enter tuşuna basın'";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{cmd}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                });
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("WATCHDOG", $"Sürüm penceresi açılamadı: {ex.Message}", true);
            }
        }
    }
}
