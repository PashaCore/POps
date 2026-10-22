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
        // configPaths: ayarın arandığı dosyalar (null: POpsHelpers.ConfigPaths). Testler kuralları ve varsayılan klasörleri
        // geçici klasörlere çevirir. Dönen: seçilen klasörler (log satırları için) ve ajanın yolları.
        internal static (FolderSettings Folders, AgentPaths Paths) Apply(bool secure, FolderRules? rules = null,
            string defaultData = FolderSettings.DefaultDataDirectory, string defaultLog = FolderSettings.DefaultLogDirectory,
            IReadOnlyList<string>? configPaths = null)
        {
            configPaths ??= POpsHelpers.ConfigPaths;
            string? data = POpsHelpers.ReadConfigText(FolderSettings.DataDirectoryKey, configPaths, out string? dataProblem);
            string? log = POpsHelpers.ReadConfigText(FolderSettings.LogDirectoryKey, configPaths, out string? logProblem);
            FolderSettings folders = FolderSettings.Resolve(data, log, rules ?? FolderRules.ForMachine(AppContext.BaseDirectory), true, defaultData, defaultLog);
            if (dataProblem != null) folders.Problems.Add($"{dataProblem}; varsayılan {defaultData} kullanılıyor");
            if (logProblem != null) folders.Problems.Add($"{logProblem}; varsayılan {defaultLog} kullanılıyor");
            if (secure) folders.SecureCustom();
            AgentPaths paths = AgentPaths.ForFolders(folders.DataDirectory, folders.LogDirectory, configPaths, folders.Problem);
            Use(paths);
            return (folders, paths);
        }

        // Süreç geneli değerler aynı klasörlere: SYSTEM bileşenlerinin log klasörü (POpsHelpers.Log; süreç başına bir kez) ve
        // Worker bölmesinin b2/b3 adımlarına kadar statik kalan yollar (AgentUpdate.DataDir, SecureStore.Dir,
        // ServerTrust.CaPath; onları kullanan kod: bkz. her birinin açıklaması)
        internal static void Use(AgentPaths paths)
        {
            POpsHelpers.MachineLogDir = paths.LogDir;
            AgentUpdate.DataDir = paths.DataDir;
            SecureStore.Dir = paths.SecureDir;
            ServerTrust.CaPath = paths.ServerCaPath;
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
