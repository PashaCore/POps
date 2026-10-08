using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace POpsAgent
{
    // Ajanın klasörleri ve dosyaları: veri klasörü (appsettings.json "DataDirectory", verilmezse C:\POpsData), güvenli depo
    // (<veri>\secure, yalnızca SYSTEM/Administrators), SYSTEM bileşenlerinin log klasörü ("LogDirectory", verilmezse
    // C:\POpsLogs), ayar dosyaları (appsettings.json) ve kurum sertifikası (server-ca.pem). Servis açılışta, ilk log
    // satırından önce bir kez seçer (bkz. AgentDirectories) ve AgentContext ile Worker'a verir; Worker'ın kurduğu servisler
    // kullandıkları yolu kurucularında alır. Testler her test için kendi geçici klasörleriyle kurar (POps.Tests.AgentHarness).
    // Değişmez: servis boyunca aynı kalır.
    [SupportedOSPlatform("windows")]
    public sealed class AgentPaths
    {
        // POpsUpdater ve POpsWatchdog ile ortak dosyalar (biçimleri AgentUpdate'te)
        private const string IdentityFile = "identity.key";
        private const string HealthFile = "health.json";
        private const string UpdateLockFile = "update.lock";
        private const string UpdateResultFile = "update-result.json";
        private const string ReportedResultFile = "update-result.reported.json";
        // Dosya aktarımının gelen kutusu ve eş önbelleği (veri klasörünün alt klasörleri)
        internal const string InboxFolder = "inbox";
        internal const string PeerCacheFolder = "cache";

        // folderProblem: klasör ayarının sorunu (varsayılan klasör kullanılıyor); null: sorun yok
        public AgentPaths(string dataDir, string secureDir, string logDir, IReadOnlyList<string> configPaths, string serverCaPath,
            string? folderProblem = null)
        {
            DataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
            SecureDir = secureDir ?? throw new ArgumentNullException(nameof(secureDir));
            LogDir = logDir ?? throw new ArgumentNullException(nameof(logDir));
            ConfigPaths = configPaths ?? throw new ArgumentNullException(nameof(configPaths));
            ServerCaPath = serverCaPath ?? throw new ArgumentNullException(nameof(serverCaPath));
            FolderProblem = folderProblem;
        }

        // Servisin yerleşimi: güvenli depo ve kurum sertifikası veri klasörünün içinde
        public static AgentPaths ForFolders(string dataDir, string logDir, IReadOnlyList<string> configPaths, string? folderProblem = null) =>
            new AgentPaths(dataDir, Path.Combine(dataDir, "secure"), logDir, configPaths,
                Path.Combine(dataDir, "secure", ServerTrust.FileName), folderProblem);

        public string DataDir { get; }
        public string SecureDir { get; }
        public string LogDir { get; }
        // Ayarın arandığı dosyalar, sırayla (ilk dolu değer kullanılır; bkz. POpsHelpers.ResolveServerUrl)
        public IReadOnlyList<string> ConfigPaths { get; }
        public string ServerCaPath { get; }
        // LogDirectory / DataDirectory geçersiz ya da kilitlenemedi (varsayılan klasör kullanılıyor); null: sorun yok
        public string? FolderProblem { get; }

        public string DataFile(string name) => Path.Combine(DataDir, name);
        public string SecureFile(string name) => Path.Combine(SecureDir, name);

        // Cihaz kimliği (HW-...)
        public string IdentityPath => DataFile(IdentityFile);
        // Güncelleme: updater'ın beklediği sağlık, çalışma kilidi, sonuç ve aşama dosyaları; indirilen paket ve updater'ın kopyası
        public string HealthPath => DataFile(HealthFile);
        public string UpdateLockPath => DataFile(UpdateLockFile);
        public string UpdateResultPath => DataFile(UpdateResultFile);
        public string ReportedResultPath => DataFile(ReportedResultFile);
        public string UpdateProgressPath => DataFile(UpdateProgressFile.FileName);
        public string UpdatesDir => DataFile("updates");
        public string UpdaterDir => DataFile("updater");
        // Geri dönüş tatbikatının işareti ve tüketilmiş hâli (güvenli depoda)
        public string RollbackDrillPath => SecureFile(RollbackDrill.MarkerFileName);
        public string ConsumedDrillPath => SecureFile(RollbackDrill.ConsumedFileName);
        // Dosya aktarımının gelen kutusu ve eş önbelleği (FileTransfer ve PeerCache b3'e kadar AgentUpdate.DataDir'den kurar)
        public string InboxDir => DataFile(InboxFolder);
        public string PeerCacheDir => DataFile(PeerCacheFolder);

        // Updater'ın komut satırı (ProcessStartInfo.ArgumentList): servisin kullandığı klasörler
        public IEnumerable<string> UpdaterArguments() =>
            [FolderSettings.DataDirectorySwitch, DataDir, FolderSettings.LogDirectorySwitch, LogDir];

        // Watchdog'un komut satırı (CreateProcessAsUser): update.lock'u aradığı veri klasörü (watchdog kullanıcı oturumunda
        // appsettings.json'u okuyamaz)
        public string WatchdogArguments() => FolderSettings.Argument(FolderSettings.DataDirectorySwitch, DataDir);
    }
}
