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
    [SupportedOSPlatform("windows")]
    internal static class WindowsUpdateAgent
    {
        public const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";

        // OperationResultCode
        private const int OrcSucceeded = 2, OrcSucceededWithErrors = 3, OrcAborted = 5;
        // UpdateOperation
        private const int UoInstallation = 1;
        // Zaman aşımında RequestAbort'tan sonra işin kapanması için tanınan ek süre
        private static readonly TimeSpan AbortGrace = TimeSpan.FromMinutes(1);

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
        }

        public static dynamic NewSession()
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true));
            try { session.ClientApplicationID = "POps Agent"; } catch { }
            return session;
        }

        private static dynamic NewCollection() => Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true));

        // Zaman uyumsuz arama: zaman aşımında RequestAbort ile gerçekten durdurulur (eşzamanlı Search durdurulamaz)
        public static Scan Search(dynamic session, TimeSpan timeout)
        {
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic job = searcher.BeginSearch(Criteria, new SearchCompletedCallback(), new UnknownWrapper(null));
            try
            {
                var clock = Stopwatch.StartNew();
                bool abortRequested = false;
                while (!(bool)job.IsCompleted)
                {
                    if (!abortRequested && clock.Elapsed > timeout)
                    {
                        job.RequestAbort();
                        abortRequested = true;
                    }
                    if (abortRequested && clock.Elapsed > timeout + AbortGrace) break;
                    Thread.Sleep(500);
                }
                if (abortRequested) throw new TimeoutException($"Windows Update taraması {timeout.TotalMinutes:0} dakikada bitmedi, durduruldu");

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

        // Seçilen güncellemeler: gerekirse EULA kabul edilir, indirilir ve kurulur. Kullanıcı girişi isteyebilenler
        // gözetimsiz kurulamayacağı için atlanır. Yeniden başlatılmaz; gerekiyorsa RebootRequired döner.
        public static InstallOutcome Install(dynamic session, Scan scan, IReadOnlyList<int> selected)
        {
            var outcome = new InstallOutcome();
            var chosen = new List<(PendingUpdate Info, dynamic Update)>();
            foreach (int index in selected)
            {
                PendingUpdate info = scan.Pending[index];
                dynamic update = scan.Handles[index];
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
                try
                {
                    dynamic downloader = session.CreateUpdateDownloader();
                    downloader.Updates = toDownload;
                    downloader.Download();
                }
                catch (Exception ex)
                {
                    // Tek tek IsDownloaded ile denetlenir; inmeyenler başarısız sayılır
                    POpsHelpers.Log("PATCH", $"İndirme hatası: {ErrorText(ex)}", true);
                }
            }

            dynamic toInstall = NewCollection();
            var installing = new List<PendingUpdate>();
            foreach (var (info, update) in chosen)
            {
                if ((bool)update.IsDownloaded) { toInstall.Add(update); installing.Add(info); }
                else outcome.Failed.Add(PatchClassifier.Label(info));
            }
            if (installing.Count == 0) return outcome;

            dynamic installer = session.CreateUpdateInstaller();
            installer.AllowSourcePrompts = false;
            try { installer.ForceQuiet = true; } catch { }
            installer.Updates = toInstall;
            dynamic result = installer.Install();
            for (int i = 0; i < installing.Count; i++)
            {
                int code = result.GetUpdateResult(i).ResultCode;
                if (code == OrcSucceeded || code == OrcSucceededWithErrors) outcome.Installed++;
                else outcome.Failed.Add(PatchClassifier.Label(installing[i]));
            }
            outcome.RebootRequired = (bool)result.RebootRequired;
            return outcome;
        }

        // COM hataları HRESULT ile anlatılır (ör. 0x80240016: başka bir kurulum sürüyor)
        public static string ErrorText(Exception ex) =>
            ex is COMException ? $"0x{ex.HResult:X8} {ex.Message}".Trim() : ex.Message;
    }

    // IUpdateSearcher.BeginSearch geri çağırma nesnesi ister (null kabul etmez); tamamlanma IsCompleted ile izlenir.
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class SearchCompletedCallback : ISearchCompletedCallback
    {
        public void Invoke(object searchJob, object callbackArgs) { }
    }

    [ComImport]
    [Guid("88AEE058-D4B0-4725-A2F1-814A67AE964C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISearchCompletedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object searchJob, [MarshalAs(UnmanagedType.IUnknown)] object callbackArgs);
    }
}
