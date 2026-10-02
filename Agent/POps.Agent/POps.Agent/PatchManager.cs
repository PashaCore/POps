using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Windows Update durumu ve sunucunun istediği kurulumlar.
    //  * scan_updates: tara, /api/patches/{hw_id} ile bildir. Günlük tarama da aynı yoldan (bkz. PatchSchedule).
    //  * install_updates (security | all): tara, seçilenleri indir ve kur (EULA kabul edilir; toplam en çok 3 saat),
    //    yeniden tara ve last_result'a kısa Türkçe özetle bildir. ASLA yeniden başlatılmaz; gerekiyorsa
    //    reboot_required bildirilir.
    //  * Aynı anda tek tarama/kurulum çalışır; o sırada gelen istek loglanıp yok sayılır.
    //  * WUA çağrıları ayrı bir arka plan thread'indedir; tarama 15 dakikada biter ya da durdurulur.
    //  * Gönderilemeyen durum C:\POpsData\patch-scan.json'da saklanır ve yalnızca GÖNDERİM yeniden denenir (30 dk'da
    //    bir); tarama günde bir kalır. Sunucuda uç yoksa (404/405) gönderim bırakılır, sonraki günlük taramada denenir.
    [SupportedOSPlatform("windows")]
    public sealed class PatchManager
    {
        public static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ScheduleTick = TimeSpan.FromMinutes(1);

        private readonly string _serverUrl;
        private readonly Func<string> _hwId;
        private readonly object _stateLock = new object();
        private int _busy;
        private DateTime? _lastAttemptUtc;
        // Son başarıyla gönderilen durum: sonraki bir tarama başarısız olursa hata last_result ile bunun üzerinden bildirilir
        private PatchStatusPayload _lastStatus;

        public PatchManager(string serverUrl, Func<string> hwId)
        {
            _serverUrl = serverUrl;
            _hwId = hwId;
            Poster = status =>
            {
                string id = _hwId();
                return AgentHttp.PostAsync(_serverUrl, AgentHttp.DevicePath("/api/patches/", id), id, status, "Windows Update durumu");
            };
        }

        // Testlerde değiştirilir: durumu sunucuya gönderen çağrı
        internal Func<PatchStatusPayload, Task<PostResult>> Poster { get; set; }

        public static string StatePath => Path.Combine(AgentUpdate.DataDir, "patch-scan.json");

        public bool IsBusy => Volatile.Read(ref _busy) == 1;

        // Sunucu komutları: hemen döner, iş arka planda yürür
        public void RequestScan() => TryRun("Windows Update taraması", () => ScanAndReportAsync(null));

        public void RequestInstall(string scope)
        {
            if (!PatchClassifier.IsValidScope(scope))
            {
                // Sunucudan gelen metin loga ham yazılmaz (satır sonuyla sahte log satırı eklenemesin)
                POpsHelpers.Log("PATCH", $"install_updates yok sayıldı: geçersiz kapsam '{LogText.Safe(scope, 20)}' (security ya da all olmalı).", true);
                return;
            }
            TryRun($"Windows güncellemeleri kurulumu ({scope})", () => InstallAndReportAsync(scope));
        }

        private bool TryRun(string what, Func<Task> work)
        {
            if (!AgentHttp.EnsureCanReport())
            {
                POpsHelpers.Log("PATCH", $"{what} istendi ama cihaz secret'ı yok; sonuç gönderilemeyeceği için başlatılmadı.", true);
                return false;
            }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                POpsHelpers.Log("PATCH", $"{what} istendi ama başka bir tarama/kurulum sürüyor; istek yok sayıldı.", true);
                return false;
            }
            _ = Task.Run(async () =>
            {
                try { await work(); }
                catch (Exception ex) { POpsHelpers.Log("PATCH", $"{what} başarısız: {ex.Message}", true); }
                finally { Volatile.Write(ref _busy, 0); }
            });
            return true;
        }

        // Günlük döngü: bekleyen gönderim varsa onu, zamanı gelmişse taramayı yapar. Tarama/kurulum sürerken sıra
        // bir sonraki tura kalır.
        public async Task ScheduleLoopAsync(CancellationToken token)
        {
            DateTime started = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(ScheduleTick, token);
                PatchState state = LoadState();
                DateTime now = DateTime.UtcNow;
                DateTime? pendingPost = state.PendingReport != null ? state.NextPostUtc ?? now : null;
                PatchStep step = PatchSchedule.NextStep(state.LastScanUtc, _lastAttemptUtc, pendingPost, started, now, _hwId());
                // Modül sunucuda kapalı: tarama yapılmaz, sonuç gönderilmez; açılınca sıradaki tur taramayı yapar
                if (step == PatchStep.Wait || !AgentHttp.EnsureCanReport() || !AgentModules.IsEnabled(AgentModules.Patches)) continue;
                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) continue;
                try
                {
                    if (step == PatchStep.RetryPost) await DeliverAsync(state.PendingReport);
                    else await ScanAndReportAsync(null);
                }
                catch (Exception ex) { POpsHelpers.Log("PATCH", $"Günlük Windows Update işlemi başarısız: {ex.Message}", true); }
                finally { Volatile.Write(ref _busy, 0); }
            }
        }

        private async Task ScanAndReportAsync(string lastResult)
        {
            _lastAttemptUtc = DateTime.UtcNow;
            PatchStatusPayload status;
            try
            {
                status = await RunOnWuaThread<PatchStatusPayload>(() =>
                {
                    dynamic session = WindowsUpdateAgent.NewSession();
                    WindowsUpdateAgent.Scan scan = WindowsUpdateAgent.Search(session, ScanTimeout);
                    return (PatchStatusPayload)StatusOf(session, scan, lastResult);
                });
            }
            catch (Exception ex)
            {
                await ReportFailureAsync($"Tarama başarısız: {WindowsUpdateAgent.ErrorText(ex)}");
                throw;
            }
            RecordScan(DateTime.UtcNow);
            await DeliverAsync(status);
        }

        private async Task InstallAndReportAsync(string scope)
        {
            _lastAttemptUtc = DateTime.UtcNow;
            POpsHelpers.Log("PATCH", $"Windows güncellemeleri kuruluyor (kapsam: {scope}); cihaz yeniden başlatılmayacak.");
            PatchStatusPayload status;
            try
            {
                status = await RunOnWuaThread<PatchStatusPayload>(() =>
                {
                    dynamic session = WindowsUpdateAgent.NewSession();
                    WindowsUpdateAgent.Scan scan = WindowsUpdateAgent.Search(session, ScanTimeout);
                    List<int> selected = PatchClassifier.SelectForInstall(scan.Pending, scope);
                    string summary;
                    if (selected.Count == 0) summary = PatchClassifier.Summarize(0, null, 0, WindowsUpdateAgent.RebootRequired());
                    else
                    {
                        try
                        {
                            WindowsUpdateAgent.InstallOutcome outcome = WindowsUpdateAgent.Install(session, scan, selected, WindowsUpdateAgent.InstallTimeout);
                            summary = PatchClassifier.Summarize(outcome.Installed, outcome.Failed, outcome.Skipped, outcome.RebootRequired || WindowsUpdateAgent.RebootRequired(), outcome.TimedOut);
                        }
                        catch (Exception ex)
                        {
                            summary = $"Kurulum yapılamadı: {WindowsUpdateAgent.ErrorText(ex)}";
                        }
                    }
                    POpsHelpers.Log("PATCH", $"Windows güncellemeleri: {summary}.");

                    // Kurulumdan sonra durum yeniden taranır (bekleyenler ve reboot_required güncel olsun)
                    try
                    {
                        dynamic after = WindowsUpdateAgent.NewSession();
                        return (PatchStatusPayload)StatusOf(after, (WindowsUpdateAgent.Scan)WindowsUpdateAgent.Search(after, ScanTimeout), summary);
                    }
                    catch (Exception ex)
                    {
                        throw new PatchRescanException(summary, ex);
                    }
                });
            }
            catch (PatchRescanException ex)
            {
                await ReportFailureAsync($"{ex.Summary}; yeniden tarama başarısız: {WindowsUpdateAgent.ErrorText(ex.InnerException)}");
                throw;
            }
            catch (Exception ex)
            {
                await ReportFailureAsync($"Kurulum yapılamadı: {WindowsUpdateAgent.ErrorText(ex)}");
                throw;
            }
            RecordScan(DateTime.UtcNow);
            await DeliverAsync(status);
        }

        private static PatchStatusPayload StatusOf(dynamic session, WindowsUpdateAgent.Scan scan, string lastResult)
        {
            DateTime? lastInstall = null;
            try { lastInstall = WindowsUpdateAgent.LastSuccessfulInstallUtc(session); }
            catch (Exception ex) { POpsHelpers.Log("PATCH", $"Windows Update geçmişi okunamadı: {WindowsUpdateAgent.ErrorText(ex)}", true); }
            return PatchClassifier.BuildStatus(scan.Pending, WindowsUpdateAgent.RebootRequired(), scan.SearchedUtc, lastInstall, lastResult);
        }

        // Durumu gönderir. Başarısızsa saklar ve yalnızca gönderimi sonra yeniden dener (bkz. PatchSchedule.NextPostUtc).
        internal async Task<PostResult> DeliverAsync(PatchStatusPayload status)
        {
            if (!AgentModules.IsEnabled(AgentModules.Patches))
            {
                // Tarama ya da kurulum sürerken modül kapandı: sonuç gönderilmez, bekleyen gönderim de bırakılır
                lock (_stateLock)
                {
                    PatchState state = LoadState();
                    state.PendingReport = null;
                    state.NextPostUtc = null;
                    SaveState(state);
                }
                POpsHelpers.Log("PATCH", "Windows Update modülü bu bilgisayarın laboratuvarında kapalı; durum gönderilmedi.");
                return PostResult.NotSent;
            }
            PostResult result = await Poster(status);
            DateTime now = DateTime.UtcNow;
            lock (_stateLock)
            {
                PatchState state = LoadState();
                DateTime? next = PatchSchedule.NextPostUtc(result, now);
                state.PendingReport = next.HasValue ? status : null;
                state.NextPostUtc = next;
                SaveState(state);
            }
            if (result == PostResult.Sent)
            {
                _lastStatus = status;
                POpsHelpers.Log("PATCH", $"Windows Update durumu gönderildi: {status.PendingCount} bekleyen ({status.PendingSecurity} güvenlik, {status.PendingCritical} kritik), yeniden başlatma {(status.RebootRequired ? "gerekiyor" : "gerekmiyor")}.");
            }
            else if (result == PostResult.EndpointMissing)
                POpsHelpers.Log("PATCH", "Sunucuda /api/patches ucu yok; durum bir sonraki günlük taramada yeniden gönderilecek.", true);
            else
                POpsHelpers.Log("PATCH", $"Windows Update durumu saklandı; gönderim {PatchSchedule.PostRetryDelay.TotalMinutes:0} dk sonra yeniden denenecek (yeniden taranmadan).");
            return result;
        }

        private void RecordScan(DateTime utc)
        {
            lock (_stateLock)
            {
                PatchState state = LoadState();
                state.LastScanUtc = utc;
                SaveState(state);
            }
        }

        // Tarama başarısızsa sayılar bilinmez: yalnızca daha önce gönderilmiş bir durum varsa hata onun üzerinden bildirilir
        private async Task ReportFailureAsync(string message)
        {
            POpsHelpers.Log("PATCH", message, true);
            PatchStatusPayload last = _lastStatus;
            if (last == null) return;
            var status = JsonSerializer.Deserialize<PatchStatusPayload>(JsonSerializer.Serialize(last));
            status.LastResult = message.Length <= 500 ? message : message.Substring(0, 497) + "...";
            status.RebootRequired = WindowsUpdateAgent.RebootRequired();
            await DeliverAsync(status);
        }

        private static Task<T> RunOnWuaThread<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            })
            { IsBackground = true, Name = "POps Windows Update" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return tcs.Task;
        }

        internal static PatchState LoadState()
        {
            try
            {
                if (File.Exists(StatePath)) return JsonSerializer.Deserialize<PatchState>(File.ReadAllText(StatePath)) ?? new PatchState();
            }
            catch { }
            return new PatchState();
        }

        internal static void SaveState(PatchState state)
        {
            try
            {
                Directory.CreateDirectory(AgentUpdate.DataDir);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
            }
            catch (Exception ex) { POpsHelpers.Log("PATCH", $"{StatePath} yazılamadı: {ex.Message}", true); }
        }

        private sealed class PatchRescanException : Exception
        {
            public PatchRescanException(string summary, Exception inner) : base(inner.Message, inner) => Summary = summary;
            public string Summary { get; }
        }
    }
}
