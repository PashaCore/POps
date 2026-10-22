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
                Console.WriteLine($" POps Agent - Sürüm: {Worker.AppVersion}");
                Console.WriteLine($"========================================\n");
                Console.ResetColor();
                return; // Uygulamayı burada bitir, hostu hiç başlatma.
            }

            // Log ve veri klasörleri appsettings.json'dan (LogDirectory / DataDirectory; bkz. AgentDirectories), ilk log
            // satırından önce, bir kez. Servis seçilen klasörleri oluşturup kilitler; --generalize aynı klasörlerdeki dosyaları
            // siler. Yollar AgentContext ile Worker'a gider.
            bool generalize = Generalizer.IsRequested(args);
            (FolderSettings folders, AgentPaths paths) = AgentDirectories.Apply(secure: !generalize, configPaths: POpsHelpers.DefaultConfigPaths);

            // İmaj öncesi temizlik: servis durdurulur, cihaza özel dosyalar silinir (bkz. Generalizer; çıkış kodları orada)
            if (generalize)
            {
                Environment.ExitCode = Generalizer.Run(args, Console.Out, paths);
                return;
            }

            // 🚀 ÇALIŞMA DİZİNİNİ EXE KONUMUNA ZORLA
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            // Log klasörü her açılışta SYSTEM/Administrators'a kilitlenir (ilk log satırından önce)
            POpsHelpers.SecureLogDirectory();

            // POpsHelpers ile sistem başlangıcını logluyoruz
            POpsHelpers.Log("AGENT", "========================================");
            POpsHelpers.Log("AGENT", $"POps Agent Başlatılıyor ({Worker.AppVersion})");
            AgentDirectories.LogChoice(folders);

            // Worker'ın yolları ve çalışma durumu (bkz. AgentContext): her parçanın servis boyunca tek örneği. Statik kalan kodun
            // süreç varsayılanları da bu örneklerdir. Güncelleme durumu tatbikat kararından önce gerekir.
            var trust = new ServerTrust(paths.ServerCaPath);
            var credentials = new CredentialStore(paths);
            var context = new AgentContext(paths,
                new CapabilityState(paths.SecureFile(AgentCapabilities.FileName), trust),
                new ModuleState(),
                credentials,
                trust,
                new AgentHttp(trust.NewHandler(), credentials),
                new AgentUpdater(paths));
            context.UseForProcess();

            // Tatbikat kararı açılışın başında alınır; health.json ancak Worker çekirdek başlangıcını tamamlayınca yazılır.
            bool suppressOperationalHealth = context.Updater.ApplyRollbackDrillOnStartup();
            if (suppressOperationalHealth)
                POpsHelpers.Log("UPDATE", "[TATBİKAT] health.json yazılmadı; updater bu sürümü sağlıksız sayıp önceki sürüme dönecek.", true);

            var builder = Host.CreateApplicationBuilder(args);

            // Ajanımızı resmi bir Windows Servisi olarak sisteme tanıtıyoruz
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "POpsAgent";
            });

            builder.Services.AddSingleton(new AgentStartupHealth(suppressOperationalHealth, context.Updater.WriteOperationalHealth));
            // Worker'ın yolları ve durumu; bkz. AgentContext
            builder.Services.AddSingleton(context);

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
