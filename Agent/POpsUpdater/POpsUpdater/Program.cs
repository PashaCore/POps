using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Runtime.Versioning;

namespace POpsUpdater
{
    [SupportedOSPlatform("windows")]
    class Program
    {
        static void Main(string[] args)
        {
            // 1. KONSOL EKRANI HAZIRLIĞI
            Console.WriteLine("========================================");
            Console.WriteLine(" POps Otomatik Guncelleyici v3 (.NET 8)");
            Console.WriteLine("========================================");

            POpsHelpers.Log("UPDATER", "Güncelleme operasyonu başlatıldı.");

            // Ajanın ortamdan tamamen çekilmesi (dosya kilitlerinin kalkması) için bekle
            Thread.Sleep(3000);

            string serviceName = "POpsAgent";
            string targetDir = args.Length > 0 ? args[0] : @"C:\POps"; // 🚀 MSI Standart Dizini
            string sourceDir = AppDomain.CurrentDomain.BaseDirectory;

            POpsHelpers.Log("UPDATER", $"Kaynak: {sourceDir} | Hedef: {targetDir}");

            // 2. SERVİSİ DURDUR
            try
            {
                using (ServiceController sc = new ServiceController(serviceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        POpsHelpers.Log("UPDATER", "Ajan servisi durduruluyor...");
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                }
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("UPDATER", $"Servis durdurma hatası (Önemli olmayabilir): {ex.Message}");
            }

            // 3. KALINTILARI ZORLA YOK ET (Dosya kilitlenmesini kesin önlemek için)
            string[] processesToKill = { "POpsAgent", "POpsWatchdog", "POpsVision" };
            foreach (var pName in processesToKill)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(pName))
                    {
                        POpsHelpers.Log("UPDATER", $"Kilitli işlem sonlandırılıyor: {pName}.exe");
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
                catch { }
            }

            // 4. DOSYALARI KOPYALA (EZE EZE)
            POpsHelpers.Log("UPDATER", "Yeni sürüm dosyaları aktarılıyor...");
            try
            {
                foreach (string newPath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
                {
                    // Updater'ın kendisini, PDB'leri ve ZIP'leri kopyalama
                    if (newPath.EndsWith(".pdb") || newPath.EndsWith(".zip") || newPath.Contains("POpsUpdater.exe") || newPath.Contains("POpsUpdater.dll"))
                        continue;

                    string destPath = newPath.Replace(sourceDir, targetDir + "\\");
                    string destFolder = Path.GetDirectoryName(destPath);

                    if (destFolder != null && !Directory.Exists(destFolder))
                        Directory.CreateDirectory(destFolder);

                    File.Copy(newPath, destPath, true);
                    Console.WriteLine($"  -> Kopyalandi: {Path.GetFileName(newPath)}");
                }
                POpsHelpers.Log("UPDATER", "Dosya aktarımı başarıyla tamamlandı.");
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("UPDATER", $"Kopyalama Hatasi: {ex.Message}", true);
            }

            // 5. SERVİSİ GERİ BAŞLAT
            try
            {
                using (ServiceController sc = new ServiceController(serviceName))
                {
                    POpsHelpers.Log("UPDATER", "Ajan servisi yeniden başlatılıyor...");
                    sc.Start();
                }
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("UPDATER", $"Servis başlatılamadı: {ex.Message}", true);
            }

            POpsHelpers.Log("UPDATER", "Güncelleme operasyonu bitti. Çıkış yapılıyor.");
            Console.WriteLine("\n[!] Guncelleme Basariyla Tamamlandi! Bu pencere otomatik kapanacaktir.");
            Thread.Sleep(3000);
        }
    }
}