#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    public enum PowerOperation { Shutdown, Restart, Logoff, Lock }

    // {"action":"power","task_id":n,"op":"shutdown"|"restart"|"logoff"|"lock","delay":0..600,"message":"..."|null,"requested_by":"..."}
    // Sunucunun şeması (docs/protocol/server-to-agent/power.json): op ve delay zorunlu; message en çok 200 karakterlik
    // tek satırlık metin ya da null (sunucu her zaman gönderir; yoksa da not yok sayılır). Sunucu doğrulamış olsa da her
    // alan burada yeniden denetlenir.
    public sealed class PowerRequest
    {
        public const int MaxDelaySeconds = 600;
        public const int MaxMessageLength = 200;

        public int TaskId { get; private init; }
        public PowerOperation Operation { get; private init; }
        public int DelaySeconds { get; private init; }
        // Kontrol karakterleri atılmış yönetici notu (tepside geri sayımın altında); yoksa boş
        public string Message { get; private init; } = "";
        public string? RequestedBy { get; private init; }

        // Protokoldeki ad (op)
        public string Op => OpName(Operation);

        // Oturum kapatma ve kilitleme oturum açık bir kullanıcı ister
        public bool NeedsUser => Operation == PowerOperation.Logoff || Operation == PowerOperation.Lock;

        public static string OpName(PowerOperation operation) => operation switch
        {
            PowerOperation.Shutdown => "shutdown",
            PowerOperation.Restart => "restart",
            PowerOperation.Logoff => "logoff",
            _ => "lock",
        };

        // Dönen: hata nedeni (Türkçe) ya da null; geçerliyse request dolu
        public static string? Parse(JsonElement root, int taskId, out PowerRequest? request)
        {
            request = null;
            PowerOperation operation;
            switch (SessionTasks.RequiredString(root, "op").Value)
            {
                case "shutdown": operation = PowerOperation.Shutdown; break;
                case "restart": operation = PowerOperation.Restart; break;
                case "logoff": operation = PowerOperation.Logoff; break;
                case "lock": operation = PowerOperation.Lock; break;
                default: return "op shutdown, restart, logoff ya da lock olmalı";
            }

            int delay = 0;
            if (!root.TryGetProperty("delay", out JsonElement delayValue) || delayValue.ValueKind != JsonValueKind.Number
                || !delayValue.TryGetInt32(out delay) || delay < 0 || delay > MaxDelaySeconds)
                return $"delay 0 ile {MaxDelaySeconds} arasında bir tamsayı (saniye) olmalı";

            var (messageValid, message) = SessionTasks.OptionalString(root, "message");
            if (!messageValid || (message != null && SessionTasks.Length(message) > MaxMessageLength))
                return $"message en çok {MaxMessageLength} karakterlik bir metin ya da null olmalı";

            request = new PowerRequest
            {
                TaskId = taskId,
                Operation = operation,
                DelaySeconds = delay,
                // Tek satır: satır sonları boşluk olur; temizlikten sonra boş kalan not yok sayılır
                Message = message == null ? "" : SessionTasks.Clean(message, keepLineBreaks: false),
                RequestedBy = SessionTasks.RequestedBy(root),
            };
            return null;
        }
    }

    // Uzaktan kapatma, yeniden başlatma, oturum kapatma ve kilitleme. Zamanlayıcı serviste: tepsi çalışmasa da süre
    // dolunca işlem yapılır; tepsi bağlıysa (ya da süre içinde bağlanırsa) geri sayımı gösterir.
    //  * Sıra: task_id -> yetenek (power_enabled; kapalıysa -5 + capability_denied) -> alanlar (geçersizse -5, yalnızca
    //    result) -> logoff/lock için konsolda oturum açık kullanıcı (yoksa -6) -> kabul (Olay Günlüğü 1130).
    //  * Süre dolunca sonuç ("[TAMAM] ...", 0) önce gönderilir (onaylı sunucuda diske de yazılır), sonra işlem yapılır:
    //    kapatma bağlantıyı keser, sonuç yeniden açılışta gönderilir.
    //  * Aynı anda tek geri sayım: aynı task_id yeniden gelirse yok sayılır (execute gibi); başka bir power gelirse
    //    öncekinin yerine geçer (önceki -2). cancel_task geri sayımı durdurur (-2); servis durursa -4.
    //  * Kilitleme: tepsi konsol oturumundaysa LockWorkStation'ı tepsi çağırır (oturum aynı kalır); tepsi yoksa ya da
    //    yanıt vermezse servis konsol oturumunu ayırır (WTSDisconnectSession: oturum ve programlar açık kalır, Windows
    //    giriş ekranını gösterir). Oturum kapatma servisten WTSLogoffSession, kapatma/yeniden başlatma shutdown.exe ile.
    [SupportedOSPlatform("windows")]
    public sealed class PowerActions
    {
        public const string Capability = "power";
        public const string ActionName = "power";
        public const string DisabledMessage = "[REDDEDİLDİ] Bu cihazda uzaktan güç işlemleri kapalı (yetenek politikası); işlem yapılmadı.";
        // Boru mesajları: servis -> tepsi
        public const string CountdownPrefix = "POWER_COUNTDOWN:";
        public const string CancelPrefix = "POWER_CANCEL:";
        public const string LockPrefix = "POWER_LOCK:";
        // tepsi -> servis: "POWER_LOCK_RESULT:<task_id>:1|0"
        public const string LockResultPrefix = "POWER_LOCK_RESULT:";

        // Gerçek işlem (bkz. WindowsPower). Testler sahtesini koyar; gerçek olan yalnızca LocalSystem'de bir şey yapar.
        internal static Func<PowerOperation, int, bool> Execute { get; set; } = WindowsPower.Run;
        // Tepsinin kilitleme yanıtı en çok bu kadar beklenir; sonra servis oturumu ayırır
        internal static TimeSpan TrayLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

        private readonly Func<int, string, int, Task> _sendResult;
        private readonly Func<int, Task> _deny;
        private readonly Action<string> _toTray;
        private readonly Func<bool> _trayInConsoleSession;
        private readonly object _gate = new object();
        private Pending? _current;

        // Olay Günlüğü (testler yakalar)
        internal Action<LocalAuditEvent> Audit { get; set; } = LocalAudit.Write;

        // sendResult(task_id, output, exit_code); deny(task_id): capability_denied power/power
        public PowerActions(Func<int, string, int, Task> sendResult, Func<int, Task> deny, Action<string> toTray, Func<bool> trayInConsoleSession)
        {
            _sendResult = sendResult;
            _deny = deny;
            _toTray = toTray;
            _trayInConsoleSession = trayInConsoleSession;
        }

        // Geri sayımı süren (ya da işlemi yapılmakta olan) görev; yoksa null
        public int? CurrentTaskId { get { lock (_gate) return _current?.Request.TaskId; } }

        public async Task HandleAsync(JsonElement root, CancellationToken serviceStopping)
        {
            int? id = SessionTasks.TaskId(root);
            if (id == null)
            {
                POpsHelpers.Log("AGENT", "power yok sayıldı: task_id tamsayı değil (sonuç bildirilemez).", true);
                return;
            }
            int taskId = id.Value;
            if (CurrentTaskId == taskId)
            {
                POpsHelpers.Log("AGENT", $"Güç işlemi zaten sürüyor; yinelenen emir yok sayıldı (TaskID: {taskId}).");
                return;
            }
            if (!AgentCapabilities.PowerEnabled)
            {
                await _sendResult(taskId, DisabledMessage, CommandRunner.ExitDenied);
                await _deny(taskId);
                return;
            }
            string? error = PowerRequest.Parse(root, taskId, out PowerRequest? request);
            if (request == null)
            {
                POpsHelpers.Log("AGENT", $"[GÜVENLİK] Geçersiz güç isteği reddedildi (TaskID: {taskId}): {error}.", true);
                await _sendResult(taskId, $"[REDDEDİLDİ] Geçersiz güç isteği: {error}.", CommandRunner.ExitDenied);
                return;
            }
            if (request.NeedsUser && !SessionTasks.HasConsoleUser())
            {
                POpsHelpers.Log("AGENT", $"Güç işlemi ({request.Op}) yapılmadı: oturum açık kullanıcı yok (TaskID: {taskId}).");
                await _sendResult(taskId, SessionTasks.NoUserOutput, SessionTasks.ExitNoUser);
                return;
            }

            Audit(LocalAudit.PowerAccepted(request.Op, request.DelaySeconds, request.RequestedBy, taskId));
            POpsHelpers.Log("AGENT", $"Güç işlemi kabul edildi: {request.Op}, {request.DelaySeconds} sn sonra (TaskID: {taskId}).");
            var pending = new Pending(request, serviceStopping);
            Pending? replaced;
            lock (_gate)
            {
                replaced = _current;
                _current = pending;
            }
            if (replaced != null && replaced.TryCancel($"yerine yeni güç işlemi (görev {taskId}) geldiği için iptal edildi"))
                POpsHelpers.Log("AGENT", $"Önceki güç işleminin geri sayımı durduruldu (TaskID: {replaced.Request.TaskId}).");
            if (request.DelaySeconds > 0) _toTray(CountdownMessage(request, request.DelaySeconds));
            // Bekleme girişi kabul anında alınır: arka plandaki görev sonradan değiştirilen girişi kullanmaz
            Func<TimeSpan, CancellationToken, Task> delay = SessionTasks.Delay;
            _ = Task.Run(() => RunAsync(pending, delay), CancellationToken.None);
        }

        // cancel_task: geri sayım sürüyorsa durur (sonuç -2 geri sayım görevinden gider). Dönen: böyle bir geri sayım vardı mı
        public bool Cancel(int taskId)
        {
            Pending? pending;
            lock (_gate) pending = _current != null && _current.Request.TaskId == taskId ? _current : null;
            return pending != null && pending.TryCancel("panelden iptal edildi");
        }

        // Yetenek kapatıldı (set_capabilities power_enabled false): süren geri sayım da durur (-2)
        public bool CancelForDisabledCapability()
        {
            Pending? pending;
            lock (_gate) pending = _current;
            return pending != null && pending.TryCancel("bu cihazda güç işlemleri kapatıldığı için iptal edildi");
        }

        // Tepsi yeniden bağlandı: süren geri sayım kalan süreyle yeniden gösterilir
        public void SyncTray()
        {
            Pending? pending;
            lock (_gate) pending = _current;
            if (pending == null || !pending.Waiting) return;
            int remaining = (int)Math.Ceiling((pending.DeadlineUtc - DateTime.UtcNow).TotalSeconds);
            if (remaining > 0) _toTray(CountdownMessage(pending.Request, remaining));
        }

        // "<task_id>:1|0": tepsi LockWorkStation'ı çağırdı mı. Dönen: bekleyen kilitleme yanıtı mıydı
        public bool OnTrayLockResult(string payload)
        {
            string[] parts = (payload ?? "").Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int taskId)) return false;
            TaskCompletionSource<bool>? reply;
            lock (_gate) reply = _current != null && _current.Request.TaskId == taskId ? _current.TrayLock : null;
            return reply != null && reply.TrySetResult(parts[1] == "1");
        }

        // Tepsiye geri sayım: "POWER_COUNTDOWN:<base64 JSON {task_id, op, seconds, message}>"
        internal static string CountdownMessage(PowerRequest request, int seconds) =>
            CountdownPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                task_id = request.TaskId,
                op = request.Op,
                seconds,
                message = request.Message,
            }));

        // Başarılı sonucun metni (işlemden hemen önce gönderilir)
        public static string DoneOutput(PowerOperation operation) => operation switch
        {
            PowerOperation.Shutdown => "[TAMAM] Bilgisayar kapatılıyor.",
            PowerOperation.Restart => "[TAMAM] Bilgisayar yeniden başlatılıyor.",
            PowerOperation.Logoff => "[TAMAM] Kullanıcının oturumu kapatılıyor.",
            _ => "[TAMAM] Bilgisayar kilitleniyor.",
        };

        private async Task RunAsync(Pending pending, Func<TimeSpan, CancellationToken, Task> delay)
        {
            PowerRequest request = pending.Request;
            int taskId = request.TaskId;
            try
            {
                try
                {
                    if (request.DelaySeconds > 0) await delay(TimeSpan.FromSeconds(request.DelaySeconds), pending.Token);
                }
                catch (OperationCanceledException)
                {
                    // Panel iptal etmediyse servis duruyor
                    pending.TryCancel(null);
                }
                if (!pending.TryStart())
                {
                    _toTray(CancelPrefix + taskId.ToString(CultureInfo.InvariantCulture));
                    if (pending.CancelReason != null)
                        await _sendResult(taskId, $"[İPTAL EDİLDİ]: Güç işlemi {pending.CancelReason}; bilgisayara dokunulmadı.", CommandRunner.ExitCancelled);
                    else
                        await _sendResult(taskId, "[DURDURULDU]: POps Agent servisi durduğu için güç işlemi yapılmadı.", CommandRunner.ExitServiceStopping);
                    return;
                }
                // Geri sayımda kullanıcı oturumu kapatmış olabilir
                if (request.NeedsUser && !SessionTasks.HasConsoleUser())
                {
                    _toTray(CancelPrefix + taskId.ToString(CultureInfo.InvariantCulture));
                    POpsHelpers.Log("AGENT", $"Güç işlemi ({request.Op}) yapılmadı: süre dolduğunda oturum açık kullanıcı yoktu (TaskID: {taskId}).");
                    await _sendResult(taskId, SessionTasks.NoUserOutput, SessionTasks.ExitNoUser);
                    return;
                }

                await _sendResult(taskId, DoneOutput(request.Operation), 0);
                bool done = request.Operation == PowerOperation.Lock && _trayInConsoleSession() && await LockThroughTrayAsync(pending);
                if (!done) done = RunPlatform(request);
                if (done) POpsHelpers.Log("AGENT", $"Güç işlemi uygulandı: {request.Op} (TaskID: {taskId}).");
                else POpsHelpers.Log("AGENT", $"[HATA] Güç işlemi uygulanamadı: {request.Op} (TaskID: {taskId}); sonuç sunucuya önceden gönderilmişti.", true);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"[HATA] Güç işlemi yürütülemedi (TaskID: {taskId}): {ex.Message}", true);
            }
            finally
            {
                lock (_gate) if (_current == pending) _current = null;
                pending.Dispose();
            }
        }

        // Tepsi kendi oturumunda LockWorkStation'ı çağırır ve "POWER_LOCK_RESULT:<id>:1|0" ile yanıtlar
        private async Task<bool> LockThroughTrayAsync(Pending pending)
        {
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) pending.TrayLock = reply;
            _toTray(LockPrefix + pending.Request.TaskId.ToString(CultureInfo.InvariantCulture));
            Task finished = await Task.WhenAny(reply.Task, Task.Delay(TrayLockTimeout));
            if (finished == reply.Task && await reply.Task) return true;
            POpsHelpers.Log("AGENT", "Tepsi ekranı kilitleyemedi ya da yanıt vermedi; konsol oturumu servisten ayrılıyor.", true);
            return false;
        }

        private static bool RunPlatform(PowerRequest request)
        {
            try { return Execute(request.Operation, request.TaskId); }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"[HATA] Güç işlemi ({request.Op}) başarısız: {ex.Message}", true);
                return false;
            }
        }

        // Geri sayımdaki görev: bekliyor -> uygulanıyor ya da iptal (kim önce değiştirirse o kazanır)
        private sealed class Pending : IDisposable
        {
            private enum State { Waiting, Running, Cancelled }

            private readonly CancellationTokenSource _cts;
            private readonly object _lock = new object();
            private State _state;
            private string? _cancelReason;

            public Pending(PowerRequest request, CancellationToken serviceStopping)
            {
                Request = request;
                DeadlineUtc = DateTime.UtcNow.AddSeconds(request.DelaySeconds);
                _cts = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping);
            }

            public PowerRequest Request { get; }
            public DateTime DeadlineUtc { get; }
            public CancellationToken Token => _cts.Token;
            public TaskCompletionSource<bool>? TrayLock { get; set; }
            public bool Waiting { get { lock (_lock) return _state == State.Waiting; } }
            // İptalin nedeni; null: servis durdu
            public string? CancelReason { get { lock (_lock) return _cancelReason; } }

            public bool TryStart()
            {
                lock (_lock)
                {
                    if (_state != State.Waiting) return false;
                    _state = State.Running;
                    return true;
                }
            }

            public bool TryCancel(string? reason)
            {
                lock (_lock)
                {
                    if (_state != State.Waiting) return false;
                    _state = State.Cancelled;
                    _cancelReason = reason;
                }
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
                return true;
            }

            public void Dispose() => _cts.Dispose();
        }
    }

    // Gerçek güç işlemleri. Yalnızca LocalSystem'de (servis) çalışır: elle ya da test sürecinde çalışan kod bilgisayarı
    // kapatamaz. Kapatma ve yeniden başlatma shutdown.exe ile (System32'den, tam yol): açık programlar beklenmeden
    // kapatılır (/f; uyarı geri sayımdır), Olay Günlüğüne "planlı" nedeniyle yazılır.
    [SupportedOSPlatform("windows")]
    internal static class WindowsPower
    {
        public static bool Run(PowerOperation operation, int taskId)
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (!identity.IsSystem)
                {
                    POpsHelpers.Log("AGENT", "Güç işlemi yapılmadı: ajan LocalSystem olarak çalışmıyor.", true);
                    return false;
                }
            }
            switch (operation)
            {
                case PowerOperation.Shutdown: return RunShutdownExe("/s", taskId);
                case PowerOperation.Restart: return RunShutdownExe("/r", taskId);
                case PowerOperation.Logoff: return OnConsoleSession(session => WTSLogoffSession(IntPtr.Zero, session, false), "WTSLogoffSession");
                default: return OnConsoleSession(session => WTSDisconnectSession(IntPtr.Zero, session, false), "WTSDisconnectSession");
            }
        }

        private static bool RunShutdownExe(string mode, int taskId)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add("/t");
            start.ArgumentList.Add("0");
            start.ArgumentList.Add("/f");
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("p:0:0");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add($"POps: yönetici isteği (görev {taskId.ToString(CultureInfo.InvariantCulture)})");
            using Process? process = Process.Start(start);
            if (process == null) return false;
            if (!process.WaitForExit(15000))
            {
                POpsHelpers.Log("AGENT", "shutdown.exe 15 sn içinde bitmedi.", true);
                return false;
            }
            if (process.ExitCode != 0) POpsHelpers.Log("AGENT", $"shutdown.exe {process.ExitCode} ile çıktı.", true);
            return process.ExitCode == 0;
        }

        private static bool OnConsoleSession(Func<uint, bool> call, string name)
        {
            uint session = UserSessionLauncher.ActiveConsoleSession();
            if (session == UserSessionLauncher.NoSession) return false;
            if (call(session)) return true;
            POpsHelpers.Log("AGENT", $"{name} başarısız (oturum {session}, Win32 {Marshal.GetLastWin32Error()}).", true);
            return false;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSLogoffSession(IntPtr server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSDisconnectSession(IntPtr server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);
    }
}
