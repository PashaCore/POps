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

            // 🚀 ÇALIŞMA DİZİNİNİ EXE KONUMUNA ZORLA
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            // POpsHelpers ile sistem başlangıcını logluyoruz
            POpsHelpers.Log("AGENT", "========================================");
            POpsHelpers.Log("AGENT", $"POps Agent Başlatılıyor (v{Worker.APP_VERSION})");

            var builder = Host.CreateApplicationBuilder(args);

            // Ajanımızı resmi bir Windows Servisi olarak sisteme tanıtıyoruz
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "POpsAgent";
            });

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