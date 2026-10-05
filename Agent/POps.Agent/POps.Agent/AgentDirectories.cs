using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace POpsAgent
{
    // Log ve veri klasörleri: appsettings.json "LogDirectory" ve "DataDirectory" (kurallar POps.Shared.FolderSettings).
    // Servis açılışta, ilk log satırından önce seçer; seçilen klasörü oluşturup kilitler. Geçersiz ya da kilitlenemeyen
    // klasörde varsayılana döner ve sorunu Olay Günlüğüne yazar (Worker.ReportConfigProblem, 1090): ajan yine açılır.
    // Updater'a --datadir/--logdir, watchdog'a --datadir ile geçirilir (watchdog kullanıcı oturumunda appsettings.json'u
    // okuyamaz). Ayar değişince eski klasördeki dosyalar taşınmaz; servis yeniden başlatılınca yeni klasör kullanılır.
    [SupportedOSPlatform("windows")]
    internal static class AgentDirectories
    {
        // Klasör ayarının sorunu (varsayılana dönüldü); null: sorun yok
        internal static string? Problem { get; set; }

        // secure: seçilen klasörler oluşturulur ve kilitlenir (servis). --generalize yalnızca yolları kullanır.
        // Testler kuralları ve varsayılan klasörleri geçici klasörlere çevirir.
        internal static FolderSettings Apply(bool secure, FolderRules? rules = null,
            string defaultData = FolderSettings.DefaultDataDirectory, string defaultLog = FolderSettings.DefaultLogDirectory)
        {
            string? data = POpsHelpers.ReadConfigText(FolderSettings.DataDirectoryKey, out string? dataProblem);
            string? log = POpsHelpers.ReadConfigText(FolderSettings.LogDirectoryKey, out string? logProblem);
            FolderSettings folders = FolderSettings.Resolve(data, log, rules ?? FolderRules.ForMachine(AppContext.BaseDirectory), true, defaultData, defaultLog);
            if (dataProblem != null) folders.Problems.Add($"{dataProblem}; varsayılan {defaultData} kullanılıyor");
            if (logProblem != null) folders.Problems.Add($"{logProblem}; varsayılan {defaultLog} kullanılıyor");
            if (secure) folders.SecureCustom();
            Use(folders.DataDirectory, folders.LogDirectory);
            Problem = folders.Problem;
            return folders;
        }

        internal static void Use(string dataDirectory, string logDirectory)
        {
            POpsHelpers.MachineLogDir = logDirectory;
            AgentUpdate.DataDir = dataDirectory;
            SecureStore.Dir = Path.Combine(dataDirectory, "secure");
            ServerTrust.CaPath = Path.Combine(dataDirectory, "secure", ServerTrust.FileName);
        }

        // Açılışta: seçilen klasörler (varsayılan değilse) ve daraltılan izinler
        internal static void LogChoice(FolderSettings folders)
        {
            if (folders.CustomData || folders.CustomLog)
                POpsHelpers.Log("AGENT", $"Klasörler (appsettings.json): veri {folders.DataDirectory}, log {folders.LogDirectory}.");
            foreach (string note in folders.Notes) POpsHelpers.Log("AGENT", note);
        }

        // Updater'ın komut satırı (ProcessStartInfo.ArgumentList): servisin kullandığı klasörler
        internal static IEnumerable<string> UpdaterArguments() =>
            [FolderSettings.DataDirectorySwitch, AgentUpdate.DataDir, FolderSettings.LogDirectorySwitch, POpsHelpers.MachineLogDir];

        // Watchdog'un komut satırı (CreateProcessAsUser): update.lock'u aradığı veri klasörü
        internal static string WatchdogArguments() => FolderSettings.Argument(FolderSettings.DataDirectorySwitch, AgentUpdate.DataDir);
    }
}
