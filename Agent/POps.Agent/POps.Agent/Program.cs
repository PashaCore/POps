using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using POpsAgent;
using System;
using System.IO;
using System.Linq;

namespace POpsAgent
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // 🚀 SÜRÜM SORGULAMA (HIZLI YOL)
            if (args.Contains("POpsV", StringComparer.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n========================================");
                Console.WriteLine($" POps Agent - Sürüm: {Worker.APP_VERSION}");
                Console.WriteLine($"========================================\n");
                Console.ResetColor();
                return; // Uygulamayı burada bitir, hostu hiç başlatma.
            }

            // İmaj öncesi temizlik: servis durdurulur, cihaza özel dosyalar silinir (bkz. Generalizer; çıkış kodları orada)
            if (Generalizer.IsRequested(args))
            {
                Environment.ExitCode = Generalizer.Run(args, Console.Out);
                return;
            }

            // 🚀 ÇALIŞMA DİZİNİNİ EXE KONUMUNA ZORLA
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            // Log klasörü her açılışta SYSTEM/Administrators'a kilitlenir (ilk log satırından önce)
            POpsHelpers.SecureLogDirectory();

            // POpsHelpers ile sistem başlangıcını logluyoruz
            POpsHelpers.Log("AGENT", "========================================");
            POpsHelpers.Log("AGENT", $"POps Agent Başlatılıyor ({Worker.APP_VERSION})");

            // Tatbikat kararı açılışın başında alınır; health.json ancak Worker çekirdek başlangıcını tamamlayınca yazılır.
            bool suppressOperationalHealth = AgentUpdate.ApplyRollbackDrillOnStartup();
            if (suppressOperationalHealth)
                POpsHelpers.Log("UPDATE", "[TATBİKAT] health.json yazılmadı; updater bu sürümü sağlıksız sayıp önceki sürüme dönecek.", true);

            var builder = Host.CreateApplicationBuilder(args);

            // Ajanımızı resmi bir Windows Servisi olarak sisteme tanıtıyoruz
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "POpsAgent";
            });

            builder.Services.AddSingleton(new AgentStartupHealth(suppressOperationalHealth));

            // Asıl beynimiz olan Worker dosyasını ayağa kaldırıyoruz
            builder.Services.AddHostedService<Worker>();

            var host = builder.Build();

            POpsHelpers.Log("AGENT", "Host servisi ayağa kaldırıldı, Worker görev başında.");
            host.Run();

            // Servis durduğunda log at
            POpsHelpers.Log("AGENT", "POps Agent servisi durduruldu.");
        }
    }
}
