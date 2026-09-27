using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

#nullable disable

namespace POpsAgent
{
    // Windows Update Agent (WUA) COM API'si, interop DLL'i olmadan (ProgID + dynamic). Bütün çağrılar tek bir arka
    // plan thread'inde yapılır (bkz. PatchManager); WebSocket döngüsü beklemez. Hiçbir çağrı yeniden başlatmaz.
    // Arama, indirme ve kurma zaman uyumsuz (Begin*/End*) çalışır: süre dolunca RequestAbort ile gerçekten durdurulur;
    // eşzamanlı Search/Download/Install durdurulamaz ve saatlerce takılabilir.
    [SupportedOSPlatform("windows")]
    internal static class WindowsUpdateAgent
    {
        public const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";
        // İndirme + kurmanın toplam üst sınırı
        public static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(3);

        // OperationResultCode
        private const int OrcSucceeded = 2, OrcSucceededWithErrors = 3, OrcAborted = 5;
        // UpdateOperation
        private const int UoInstallation = 1;
        // Zaman aşımında RequestAbort'tan sonra işin kapanması için tanınan ek süre; kapanmazsa beklenmez
        private static readonly TimeSpan AbortGrace = TimeSpan.FromMinutes(2);

        public sealed class Scan
        {
            public List<PendingUpdate> Pending { get; } = new List<PendingUpdate>();
            // Pending ile aynı sırada IUpdate nesneleri (kurulum için)
            public List<object> Handles { get; } = new List<object>();
            public DateTime SearchedUtc { get; set; }
        }

        public sealed class InstallOutcome
        {
            public int Installed { get; set; }
            public List<string> Failed { get; } = new List<string>();
            public int Skipped { get; set; }
            public bool RebootRequired { get; set; }
            public bool TimedOut { get; set; }
        }

        // Tek bir işin (tarama ya da indirme + kurma) süresi
        private sealed class Budget
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly TimeSpan _limit;
            public Budget(TimeSpan limit) => _limit = limit;
            public bool Expired => _clock.Elapsed > _limit;
        }

        public static dynamic NewSession()
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true));
            try { session.ClientApplicationID = "POps Agent"; } catch { }
            return session;
        }

        private static dynamic NewCollection() => Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true));

        // İş bitene ya da süre dolana kadar bekler. Süre dolunca RequestAbort; iş AbortGrace içinde kapanmazsa
        // beklemeyi bırakır (thread ve "meşgul" bayrağı serbest kalır). Dönen: süre doldu mu
        private static bool WaitForJob(dynamic job, Budget budget)
        {
            Stopwatch sinceAbort = null;
            while (!(bool)job.IsCompleted)
            {
                if (sinceAbort == null && budget.Expired)
                {
                    try { job.RequestAbort(); } catch { }
                    sinceAbort = Stopwatch.StartNew();
                }
                if (sinceAbort != null && sinceAbort.Elapsed > AbortGrace) break;
                Thread.Sleep(500);
            }
            return sinceAbort != null;
        }

        public static Scan Search(dynamic session, TimeSpan timeout)
        {
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic job = searcher.BeginSearch(Criteria, new WuaCallback(), new UnknownWrapper(null));
            try
            {
                if (WaitForJob(job, new Budget(timeout))) throw new TimeoutException($"Windows Update taraması {timeout.TotalMinutes:0} dakikada bitmedi, durduruldu");

                dynamic result = searcher.EndSearch(job);
                int code = result.ResultCode;
                if (code == OrcAborted) throw new TimeoutException("Windows Update taraması durduruldu");
                if (code != OrcSucceeded && code != OrcSucceededWithErrors) throw new InvalidOperationException($"Windows Update taraması başarısız (sonuç kodu {code})");

                var scan = new Scan { SearchedUtc = DateTime.UtcNow };
                dynamic updates = result.Updates;
                int count = updates.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic update = updates.Item(i);
                    scan.Pending.Add(DescribeUpdate(update));
                    scan.Handles.Add(update);
                }
                return scan;
            }
            finally
            {
                try { job.CleanUp(); } catch { }
            }
        }

        private static PendingUpdate DescribeUpdate(dynamic update)
        {
            var pending = new PendingUpdate
            {
                Title = (string)update.Title,
                Severity = (string)update.MsrcSeverity,
            };
            dynamic kbs = update.KBArticleIDs;
            if ((int)kbs.Count > 0) pending.Kb = "KB" + (string)kbs.Item(0);
            dynamic categories = update.Categories;
            int count = categories.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic category = categories.Item(i);
                pending.Categories.Add((string)category.Name);
                pending.CategoryIds.Add((string)category.CategoryID);
            }
            try { pending.NeedsUserInput = (bool)update.InstallationBehavior.CanRequestUserInput; } catch { }
            // Okunamazsa isteğe bağlı sayılır: emin olunamayan güncelleme kurulmaz
            try { pending.BrowseOnly = (bool)update.BrowseOnly; } catch { pending.BrowseOnly = true; }
            return pending;
        }

        // Geçmişteki en son başarılı kurulum (UTC; WUA geçmiş zamanları UTC'dir)
        public static DateTime? LastSuccessfulInstallUtc(dynamic session)
        {
            dynamic searcher = session.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();
            if (total <= 0) return null;
            dynamic history = searcher.QueryHistory(0, Math.Min(total, 200));
            DateTime? latest = null;
            int count = history.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic entry = history.Item(i);
                if ((int)entry.Operation != UoInstallation || (int)entry.ResultCode != OrcSucceeded) continue;
                DateTime date = DateTime.SpecifyKind((DateTime)entry.Date, DateTimeKind.Utc);
                if (latest == null || date > latest) latest = date;
            }
            return latest;
        }

        public static bool RebootRequired()
        {
            try
            {
                dynamic info = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", true));
                if ((bool)info.RebootRequired) return true;
            }
            catch { }
            try
            {
                using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                return key != null;
            }
            catch { return false; }
        }

        // Seçilen güncellemeler: gerekirse EULA kabul edilir, indirilir ve kurulur; toplam en çok `timeout`. Kullanıcı
        // girişi isteyebilenler gözetimsiz kurulamayacağı için, isteğe bağlı ve sürüm yükseltmesi olanlar hiç
        // kurulmaz (seçimde de elenir). Yeniden başlatılmaz; gerekiyorsa RebootRequired döner.
        public static InstallOutcome Install(dynamic session, Scan scan, IReadOnlyList<int> selected, TimeSpan timeout)
        {
            var budget = new Budget(timeout);
            var outcome = new InstallOutcome();
            var chosen = new List<(PendingUpdate Info, dynamic Update)>();
            foreach (int index in selected)
            {
                PendingUpdate info = scan.Pending[index];
                dynamic update = scan.Handles[index];
                if (!PatchClassifier.IsManaged(info)) continue;
                if (info.NeedsUserInput) { outcome.Skipped++; continue; }
                try
                {
                    if (!(bool)update.EulaAccepted) update.AcceptEula();
                    chosen.Add((info, update));
                }
                catch (Exception ex)
                {
                    POpsHelpers.Log("PATCH", $"{PatchClassifier.Label(info)}: lisans kabul edilemedi ({ErrorText(ex)}).", true);
                    outcome.Failed.Add(PatchClassifier.Label(info));
                }
            }
            if (chosen.Count == 0) return outcome;

            dynamic toDownload = NewCollection();
            foreach (var (_, update) in chosen)
                if (!(bool)update.IsDownloaded) toDownload.Add(update);
            if ((int)toDownload.Count > 0)
            {
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = toDownload;
                dynamic job = downloader.BeginDownload(new WuaCallback(), new WuaCallback(), new UnknownWrapper(null));
                try
                {
                    if (WaitForJob(job, budget))
                    {
                        outcome.TimedOut = true;
                        POpsHelpers.Log("PATCH", "Güncellemelerin indirilmesi süresinde bitmedi, durduruldu.", true);
                    }
                    else
                    {
                        // Sonuç tek tek IsDownloaded ile denetlenir; inmeyenler başarısız sayılır
                        try { downloader.EndDownload(job); }
                        catch (Exception ex) { POpsHelpers.Log("PATCH", $"İndirme hatası: {ErrorText(ex)}", true); }
                    }
                }
                finally
                {
                    try { job.CleanUp(); } catch { }
                }
            }

            dynamic toInstall = NewCollection();
            var installing = new List<PendingUpdate>();
            foreach (var (info, update) in chosen)
            {
                if (!outcome.TimedOut && (bool)update.IsDownloaded) { toInstall.Add(update); installing.Add(info); }
                else outcome.Failed.Add(PatchClassifier.Label(info));
            }
            if (installing.Count == 0) return outcome;

            dynamic installer = session.CreateUpdateInstaller();
            installer.AllowSourcePrompts = false;
            try { installer.ForceQuiet = true; } catch { }
            installer.Updates = toInstall;
            dynamic installJob = installer.BeginInstall(new WuaCallback(), new WuaCallback(), new UnknownWrapper(null));
            try
            {
                bool timedOut = WaitForJob(installJob, budget);
                dynamic result = null;
                if (!timedOut || (bool)installJob.IsCompleted)
                {
                    try { result = installer.EndInstall(installJob); }
                    catch when (timedOut) { }
                }
                if (timedOut)
                {
                    outcome.TimedOut = true;
                    POpsHelpers.Log("PATCH", "Güncellemelerin kurulumu süresinde bitmedi, durduruldu.", true);
                }
                if (result == null)
                {
                    foreach (PendingUpdate info in installing) outcome.Failed.Add(PatchClassifier.Label(info));
                    return outcome;
                }
                for (int i = 0; i < installing.Count; i++)
                {
                    int code = result.GetUpdateResult(i).ResultCode;
                    if (code == OrcSucceeded || code == OrcSucceededWithErrors) outcome.Installed++;
                    else outcome.Failed.Add(PatchClassifier.Label(installing[i]));
                }
                outcome.RebootRequired = (bool)result.RebootRequired;
                return outcome;
            }
            finally
            {
                try { installJob.CleanUp(); } catch { }
            }
        }

        // COM hataları HRESULT ile anlatılır (ör. 0x80240016: başka bir kurulum sürüyor)
        public static string ErrorText(Exception ex) =>
            ex is COMException ? $"0x{ex.HResult:X8} {ex.Message}".Trim() : ex.Message;
    }

    // WUA'nın Begin* çağrıları geri çağırma nesnesi ister (null kabul etmez); tamamlanma IsCompleted ile izlenir.
    // Arayüz kimlikleri wuapi.dll'in tür kitaplığından alınmıştır.
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class WuaCallback :
        ISearchCompletedCallback,
        IDownloadProgressChangedCallback,
        IDownloadCompletedCallback,
        IInstallationProgressChangedCallback,
        IInstallationCompletedCallback
    {
        void ISearchCompletedCallback.Invoke(object job, object args) { }
        void IDownloadProgressChangedCallback.Invoke(object job, object args) { }
        void IDownloadCompletedCallback.Invoke(object job, object args) { }
        void IInstallationProgressChangedCallback.Invoke(object job, object args) { }
        void IInstallationCompletedCallback.Invoke(object job, object args) { }
    }

    [ComImport, Guid("88AEE058-D4B0-4725-A2F1-814A67AE964C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISearchCompletedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }

    [ComImport, Guid("8C3F1CDD-6173-4591-AEBD-A56A53CA77C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDownloadProgressChangedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }

    [ComImport, Guid("77254866-9F5B-4C8E-B9E2-C77A8530D64B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDownloadCompletedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }

    [ComImport, Guid("E01402D5-F8DA-43BA-A012-38894BD048F1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInstallationProgressChangedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }

    [ComImport, Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInstallationCompletedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args);
    }
}
