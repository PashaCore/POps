using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices; // FreeConsole için ekledik

namespace POpsWatchDog
{
    class Program
    {
        // Sürüm kök VERSION dosyasından gelir (Directory.Build.props -> assembly). Elle güncellenmez.
        public static readonly string APP_VERSION = POpsHelpers.AppVersion;

        // 🚀 Windows 11 Terminal kalıntılarını öldürmek için son çare
        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();

        // Ayarlar DOĞRU dosya isimlerine göre güncellendi! (Core YOK)
        static readonly string AgentServiceName = "POpsAgent";
        static readonly string VisionExeName = "POpsTray";
        static readonly string VisionExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "POpsTray.exe");


        // Main artık sadece asenkron değil, aynı zamanda gizlilik kalkanıyla sarılı
        static void Main(string[] args)
        {
            // Windows 11 Terminali tamamen koparıp atar. Eğer bir konsol açılmaya çalıştıysa bile yok eder.
            FreeConsole();

            if (args != null && args.Contains("POpsV", StringComparer.OrdinalIgnoreCase))
            {
                SpawnVersionWindow();
                return;
            }

            // Asenkron metodları senkron Main içinde başlatıp kilitliyoruz.
            // Bu sayede Windows "Form nerede?" diye sormadan arkaplanda sonsuza dek çalışır.
            Task.Run(() => RunGhostSergeantAsync()).GetAwaiter().GetResult();
        }

        static async Task RunGhostSergeantAsync()
        {
            POpsHelpers.Log("WATCHDOG", $"Hayalet Çavuş Uyandı. Versiyon: {APP_VERSION}");

            // Watchdog yalnızca ajan servisini ve tepsi uygulamasını ayakta tutar. Sunucuya bağlanmaz:
            // eski "telsiz" döngüsü backend'de hiç olmayan /ws/watchdog ucuna 15 saniyede bir bağlanmaya çalışıyordu.
            await PatrolLoopAsync();
        }

        // ================================================================
        // 1. MOTOR: DEVRİYE GÖREVİ (Sistem Kontrolü)
        // ================================================================
        private static async Task PatrolLoopAsync()
        {
            while (true)
            {
                try
                {
                    CheckAndRepairVisionProcess();
                    CheckAndRepairAgentService();
                }
                catch (Exception ex)
                {
                    POpsHelpers.Log("WATCHDOG", $"Devriye Hatası: {ex.Message}", true);
                }

                await Task.Delay(10000); // 10 saniyede bir kontrol
            }
        }


        private static void CheckAndRepairVisionProcess()
        {
            try
            {
                var processes = Process.GetProcessesByName(VisionExeName);
                if (processes.Length == 0)
                {
                    if (File.Exists(VisionExePath))
                    {
                        POpsHelpers.Log("WATCHDOG", "Gözler kapalı, POpsVision zorla başlatılıyor...");
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = VisionExePath,
                            UseShellExecute = true, // Shell execute true olmalı ki kendi izole ortamını kursun
                            CreateNoWindow = true,  // Vision'un kendisinin de gizli başlamasını garanti eder
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        Process.Start(psi);
                    }
                    else
                    {
                        POpsHelpers.Log("WATCHDOG", $"HATA: {VisionExePath} bulunamadı. Gözler kör!", true);
                    }
                }
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("WATCHDOG", $"Vision Başlatma Hatası: {ex.Message}", true);
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
                        POpsHelpers.Log("WATCHDOG", "Ajan servisi durmuş, elektroşok veriliyor (Start)...");
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    }
                }
            }
            catch
            {
                // Admin değilse veya servis yoksa sessizce yutar.
            }
        }

        static void SpawnVersionWindow()
        {
            try
            {
                string cmd = $"$Host.UI.RawUI.WindowTitle = 'POpsWatchdog Guard'; " +
                             $"Write-Host '========================================' -ForegroundColor Cyan; " +
                             $"Write-Host ' POpsWatchdog Guard - Versiyon: {APP_VERSION}' -ForegroundColor Green; " +
                             $"Write-Host '========================================' -ForegroundColor Cyan; " +
                             $"Write-Host 'Görev: POpsAgent ve POpsVision süreçlerini korur.' -ForegroundColor Gray; " +
                             $"Write-Host 'Durum: Aktif, Çift Motorlu ve Hayalet Modda (Görünmez)' -ForegroundColor Yellow; " +
                             $"Read-Host 'Kapatmak için Enter tuşuna basın'";

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{cmd}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                Process.Start(psi);
            }
            catch { }
        }
    }
}