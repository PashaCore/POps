using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace POpsAgent
{
    // Log ve veri klasörleri: appsettings.json "LogDirectory" ve "DataDirectory" (kurallar POps.Shared.FolderSettings).
    // Servis açılışta, ilk log satırından önce bir kez seçer; seçilen klasörü oluşturup kilitler. Geçersiz ya da
    // kilitlenemeyen klasörde varsayılana döner ve sorunu Olay Günlüğüne yazar (Worker.ReportConfigProblem, 1090): ajan yine
    // açılır. Seçilen yollar AgentPaths olarak AgentContext'e girer (bkz. Program). Updater'a --datadir/--logdir, watchdog'a
    // --datadir ile geçirilir (watchdog kullanıcı oturumunda appsettings.json'u okuyamaz; bkz. AgentPaths.UpdaterArguments).
    // Ayar değişince eski klasördeki dosyalar taşınmaz; servis yeniden başlatılınca yeni klasör kullanılır.
    [SupportedOSPlatform("windows")]
    internal static class AgentDirectories
    {
        // secure: seçilen klasörler oluşturulur ve kilitlenir (servis). --generalize yalnızca yolları kullanır.
        // configPaths: ayarın arandığı dosyalar (servis: POpsHelpers.DefaultConfigPaths). Testler ayar dosyalarını, kuralları ve
        // varsayılan klasörleri geçici klasörlere çevirir. Dönen: seçilen klasörler (log satırları için) ve ajanın yolları.
        internal static (FolderSettings Folders, AgentPaths Paths) Apply(bool secure, IReadOnlyList<string> configPaths, FolderRules? rules = null,
            string defaultData = FolderSettings.DefaultDataDirectory, string defaultLog = FolderSettings.DefaultLogDirectory)
        {
            ArgumentNullException.ThrowIfNull(configPaths);
            string? data = POpsHelpers.ReadConfigText(FolderSettings.DataDirectoryKey, configPaths, out string? dataProblem);
            string? log = POpsHelpers.ReadConfigText(FolderSettings.LogDirectoryKey, configPaths, out string? logProblem);
            FolderSettings folders = FolderSettings.Resolve(data, log, rules ?? FolderRules.ForMachine(AppContext.BaseDirectory), true, defaultData, defaultLog);
            if (dataProblem != null) folders.Problems.Add($"{dataProblem}; varsayılan {defaultData} kullanılıyor");
            if (logProblem != null) folders.Problems.Add($"{logProblem}; varsayılan {defaultLog} kullanılıyor");
            if (secure) folders.SecureCustom();
            AgentPaths paths = AgentPaths.ForFolders(folders.DataDirectory, folders.LogDirectory, configPaths, folders.Problem);
            return (folders, paths);
        }

        // Süreç geneli tek değer seçilen klasöre: SYSTEM bileşenlerinin log klasörü (POpsHelpers.Log; Program bir kez, ilk log
        // satırından önce çağırır). Diğer yollar bağlamın AgentPaths'indedir (bkz. Program).
        internal static void Use(AgentPaths paths)
        {
            POpsHelpers.MachineLogDir = paths.LogDir;
        }

        // Açılışta: seçilen klasörler (varsayılan değilse) ve daraltılan izinler
        internal static void LogChoice(FolderSettings folders)
        {
            if (folders.CustomData || folders.CustomLog)
                POpsHelpers.Log("AGENT", $"Klasörler (appsettings.json): veri {folders.DataDirectory}, log {folders.LogDirectory}.");
            foreach (string note in folders.Notes) POpsHelpers.Log("AGENT", note);
        }
    }
}
