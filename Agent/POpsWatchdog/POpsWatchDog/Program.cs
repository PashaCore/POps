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
        // 🚀 SÜRÜM BİLGİSİ
        public const string APP_VERSION = "2.0.1-GHOST-SERGEANT";

        // 🚀 Windows 11 Terminal kalıntılarını öldürmek için son çare
        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();

        // Ayarlar DOĞRU dosya isimlerine göre güncellendi! (Core YOK)
        static readonly string AgentServiceName = "POpsAgent";
        static readonly string VisionExeName = "POpsTray";
        static readonly string VisionExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "POpsTray.exe");

        static string _hwId = "UNKNOWN";
        static string _serverUrl = "http://127.0.0.1:8000"; // Fallback, Helpers'tan güncellenecek

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

            // Yapılandırma Bilgilerini Al
            _serverUrl = POpsHelpers.GetServerUrl();
            _hwId = POpsHelpers.GetHardwareId();

            // 2. KORUMA DÖNGÜSÜ VE AĞ DİNLEME (ÇİFT MOTOR)
            // A - Görev 1: Sistemleri sürekli kontrol et (Eski bekçi görevi)
            var patrolTask = Task.Run(() => PatrolLoopAsync());

            // B - Görev 2: Sunucuyla telsiz bağlantısı kur (Yeni çavuş görevi)
            var radioTask = Task.Run(() => RadioCommsLoopAsync());

            // Ana threadi canlı tutmak için bekleriz.
            await Task.WhenAll(patrolTask, radioTask);
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
                    if (IsWatchDogPaused())
                    {
                        POpsHelpers.Log("WATCHDOG", "Koruyucu uyku modunda (Kullanıcı tarafından duraklatıldı).", false);
                    }
                    else
                    {
                        CheckAndRepairVisionProcess();
                        CheckAndRepairAgentService();
                    }
                }
                catch (Exception ex)
                {
                    POpsHelpers.Log("WATCHDOG", $"Devriye Hatası: {ex.Message}", true);
                }

                await Task.Delay(10000); // 10 saniyede bir kontrol
            }
        }

        private static bool IsWatchDogPaused()
        {
            try
            {
                string pauseFile = @"C:\POpsData\watchdog_pause.flag";
                if (File.Exists(pauseFile))
                {
                    var lastWrite = File.GetLastWriteTime(pauseFile);
                    if ((DateTime.Now - lastWrite).TotalMinutes < 15)
                    {
                        return true;
                    }
                    else
                    {
                        File.Delete(pauseFile); // Süresi dolmuş, sil
                    }
                }
            }
            catch { }
            return false;
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

        // ================================================================
        // 2. MOTOR: TELSİZ HABERLEŞMESİ (WebSocket)
        // ================================================================
        private static async Task RadioCommsLoopAsync()
        {
            if (_hwId == "HW-UNKNOWN")
            {
                POpsHelpers.Log("WATCHDOG", "Kimlik yok, telsiz bağlantısı iptal.", true);
                return;
            }

            string wsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://") + $"/ws/watchdog/{_hwId}";

            while (true)
            {
                using (var ws = new ClientWebSocket())
                {
                    try
                    {
                        await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                        POpsHelpers.Log("WATCHDOG", "🟢 Telsiz bağlantısı (WebSocket) kuruldu.");

                        var buffer = new byte[4096];
                        while (ws.State == WebSocketState.Open)
                        {
                            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close) break;

                            string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            ProcessRadioCommand(message);
                        }
                    }
                    catch
                    {
                        // Bağlantı koparsa veya kurulamazsa sessizce bekler.
                    }
                }
                await Task.Delay(15000); // 15 saniye bekle, tekrar dene
            }
        }

        private static void ProcessRadioCommand(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("action", out var actionProp))
                {
                    string action = actionProp.GetString();
                    POpsHelpers.Log("WATCHDOG", $"Merkezden Emir Geldi: [{action}]");

                    switch (action)
                    {
                        case "restart_agent":
                            RestartService(AgentServiceName);
                            break;
                        case "kill_vision":
                            KillProcess(VisionExeName);
                            break;
                        case "restart_pc":
                            Process.Start(new ProcessStartInfo("shutdown", "/r /f /t 0") { CreateNoWindow = true, UseShellExecute = false });
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("WATCHDOG", $"Emir işleme hatası: {ex.Message}", true);
            }
        }

        private static void RestartService(string sName)
        {
            try
            {
                using (ServiceController sc = new ServiceController(sName))
                {
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                    sc.Start();
                    POpsHelpers.Log("WATCHDOG", $"{sName} servisi başarıyla yeniden başlatıldı.");
                }
            }
            catch (Exception ex) { POpsHelpers.Log("WATCHDOG", $"Servis restart hatası: {ex.Message}", true); }
        }

        private static void KillProcess(string pName)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(pName)) p.Kill();
                POpsHelpers.Log("WATCHDOG", $"{pName} süreçleri zorla sonlandırıldı.");
            }
            catch (Exception ex) { POpsHelpers.Log("WATCHDOG", $"Süreç kapatma hatası: {ex.Message}", true); }
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