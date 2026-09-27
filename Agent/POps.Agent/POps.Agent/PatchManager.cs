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
    //  * install_updates (security | all): tara, seçilenleri indir ve kur (EULA kabul edilir), yeniden tara ve
    //    last_result'a kısa Türkçe özetle bildir. ASLA yeniden başlatılmaz; gerekiyorsa reboot_required bildirilir.
    //  * Aynı anda tek tarama/kurulum çalışır; o sırada gelen istek loglanıp yok sayılır.
    //  * WUA çağrıları ayrı bir arka plan thread'indedir; tarama 15 dakikada biter ya da durdurulur.
    [SupportedOSPlatform("windows")]
    public sealed class PatchManager
    {
        public static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ScheduleTick = TimeSpan.FromMinutes(1);

        private readonly string _serverUrl;
        private readonly Func<string> _hwId;
        private int _busy;
        private DateTime? _lastAttemptUtc;
        // Son başarılı taramanın durumu: sonraki bir tarama başarısız olursa hata last_result ile bunun üzerinden bildirilir
        private PatchStatusPayload _lastStatus;

        public PatchManager(string serverUrl, Func<string> hwId)
        {
            _serverUrl = serverUrl;
            _hwId = hwId;
        }

        public static string StatePath => Path.Combine(AgentUpdate.DataDir, "patch-scan.json");

        public bool IsBusy => Volatile.Read(ref _busy) == 1;

        // Sunucu komutları: hemen döner, iş arka planda yürür
        public void RequestScan() => TryRun("Windows Update taraması", () => ScanAndReportAsync(null));

        public void RequestInstall(string scope)
        {
            if (!PatchClassifier.IsValidScope(scope))
            {
                POpsHelpers.Log("PATCH", $"install_updates yok sayıldı: geçersiz kapsam '{scope}' (security ya da all olmalı).", true);
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

        // Günlük tarama. Tarama sürerken (ör. sunucunun istediği kurulum) sıra bir sonraki tura kalır.
        public async Task ScheduleLoopAsync(CancellationToken token)
        {
            DateTime started = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(ScheduleTick, token);
                DateTime now = DateTime.UtcNow;
                if (now < PatchSchedule.NextScanUtc(LoadLastReportedScanUtc(), _lastAttemptUtc, started, now, _hwId())) continue;
                if (!AgentHttp.EnsureCanReport()) continue;
                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) continue;
                try { await ScanAndReportAsync(null); }
                catch (Exception ex) { POpsHelpers.Log("PATCH", $"Günlük Windows Update taraması başarısız: {ex.Message}", true); }
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
            await ReportAsync(status);
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
                            WindowsUpdateAgent.InstallOutcome outcome = WindowsUpdateAgent.Install(session, scan, selected);
                            summary = PatchClassifier.Summarize(outcome.Installed, outcome.Failed, outcome.Skipped, outcome.RebootRequired || WindowsUpdateAgent.RebootRequired());
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
            await ReportAsync(status);
        }

        private static PatchStatusPayload StatusOf(dynamic session, WindowsUpdateAgent.Scan scan, string lastResult)
        {
            DateTime? lastInstall = null;
            try { lastInstall = WindowsUpdateAgent.LastSuccessfulInstallUtc(session); }
            catch (Exception ex) { POpsHelpers.Log("PATCH", $"Windows Update geçmişi okunamadı: {WindowsUpdateAgent.ErrorText(ex)}", true); }
            return PatchClassifier.BuildStatus(scan.Pending, WindowsUpdateAgent.RebootRequired(), scan.SearchedUtc, lastInstall, lastResult);
        }

        private async Task ReportAsync(PatchStatusPayload status)
        {
            string hwId = _hwId();
            if (!await AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/patches/", hwId), hwId, status, "Windows Update durumu")) return;
            _lastStatus = status;
            SaveLastReportedScanUtc(DateTime.UtcNow);
            POpsHelpers.Log("PATCH", $"Windows Update durumu gönderildi: {status.PendingCount} bekleyen ({status.PendingSecurity} güvenlik, {status.PendingCritical} kritik), yeniden başlatma {(status.RebootRequired ? "gerekiyor" : "gerekmiyor")}.");
        }

        // Tarama başarısızsa sayılar bilinmez: yalnızca daha önce başarılı bir tarama varsa hata onun üzerinden bildirilir
        private async Task ReportFailureAsync(string message)
        {
            POpsHelpers.Log("PATCH", message, true);
            PatchStatusPayload last = _lastStatus;
            if (last == null) return;
            var status = JsonSerializer.Deserialize<PatchStatusPayload>(JsonSerializer.Serialize(last));
            status.LastResult = message.Length <= 500 ? message : message.Substring(0, 497) + "...";
            status.RebootRequired = WindowsUpdateAgent.RebootRequired();
            string hwId = _hwId();
            await AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/patches/", hwId), hwId, status, "Windows Update durumu");
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

        // Son BAŞARIYLA bildirilen taramanın zamanı; servis yeniden başlayınca günlük tarama tekrarlanmasın
        internal static DateTime? LoadLastReportedScanUtc()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
                return doc.RootElement.TryGetProperty("last_reported_scan_utc", out var p) && p.TryGetDateTime(out DateTime t)
                    ? DateTime.SpecifyKind(t.ToUniversalTime(), DateTimeKind.Utc)
                    : null;
            }
            catch { return null; }
        }

        internal static void SaveLastReportedScanUtc(DateTime utc)
        {
            try
            {
                Directory.CreateDirectory(AgentUpdate.DataDir);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(new Dictionary<string, string> { ["last_reported_scan_utc"] = PatchClassifier.Iso(utc) }));
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
