#nullable enable

using System;
using System.ComponentModel;
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
    //  * Süre dolunca işlem yapılır ve sonuç işlemin gerçek sonucundan gönderilir (bkz. PowerOutcome):
    //    - oturum kapatma ve kilitleme bağlantıyı kesmez: önce işlem, sonra sonuç. Başarı 0 ("[TAMAM] ..."), başarısızlık
    //      Win32 hata kodu ve "[HATA] ...".
    //    - kapatma ve yeniden başlatma bağlantıyı keser: "[TAMAM] ..." önce gönderilmemiş olarak diske yazılır (ResultOutbox
    //      Store; heartbeat de göndermez), sonra shutdown.exe çalışır ve çıkış kodu beklenir (kapatmayı başlatıp hemen
    //      döner). 0: sonuç gönderilmeye çalışılır, gönderilemezse yeniden açılışta diskten gider. 0 değilse diskteki sonuç
    //      "[HATA] shutdown.exe <kod>: ..." ile değiştirilip hemen gönderilir; bilgisayar açık kalır, başka bir şey yapılmaz.
    //  * Aynı anda tek geri sayım: aynı task_id yeniden gelirse yok sayılır (execute gibi); başka bir power gelirse
    //    öncekinin yerine geçer (önceki -2). cancel_task geri sayımı durdurur (-2); servis durursa -4.
    //  * Kilitleme: tepsi konsol oturumundaysa LockWorkStation'ı tepsi çağırır (oturum aynı kalır) ve TrayLockTimeout içinde
    //    "1" ile onaylarsa kilitleme yapılmış sayılır. Tepsi yoksa, "0" derse ya da süre içinde yanıt vermezse servis konsol
    //    oturumunu ayırır (WTSDisconnectSession: oturum ve programlar açık kalır, Windows giriş ekranını gösterir); sonuç o
    //    çağrınınkidir. Tepsinin onaylamadığı ve ayırmanın da başarısız olduğu kilitleme hatadır (geç gelen onay yok
    //    sayılır). Oturum kapatma servisten WTSLogoffSession, kapatma/yeniden başlatma shutdown.exe ile.
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

        // Kilitlemenin sonuç metni: tepsi kilitledi ya da servis konsol oturumunu ayırdı
        public const string LockedOutput = "[TAMAM] Bilgisayar kilitlendi.";
        public const string DisconnectedOutput = "[TAMAM] Bilgisayar kilitlendi: konsol oturumu ayrıldı, Windows giriş ekranını gösteriyor (programlar açık).";

        private readonly TaskResults _results;
        private readonly Func<int, Task> _deny;
        private readonly Action<string> _toTray;
        private readonly Func<bool> _trayInConsoleSession;
        private readonly CapabilityState _capabilities;
        // Gerçek işlem (servis: WindowsPower, yalnızca LocalSystem'de bir şey yapar) ve konsol oturumunda kullanıcı var mı
        // (bkz. MachineSeams.Power, HasConsoleUser)
        private readonly Func<PowerOperation, int, PowerOutcome> _platform;
        private readonly Func<bool> _hasConsoleUser;
        private readonly object _gate = new object();
        private Pending? _current;

        // Olay Günlüğü (testler yakalar)
        internal Action<LocalAuditEvent> Audit { get; set; } = LocalAudit.Write;
        // Geri sayım beklemesi (testler anında ya da elle biter) ve tepsinin kilitleme yanıtının en uzun beklemesi (sonra servis
        // oturumu ayırır). Servis değiştirmez.
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
        internal TimeSpan TrayLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

        // results: görev sonuçlarının gönderimi ve saklanması (ResultOutbox); deny(task_id): capability_denied power/power;
        // capabilities: bağlamın yetenek durumu; platform: güç işleminin kendisi; hasConsoleUser: etkin konsol oturumunda
        // oturum açmış kullanıcı var mı
        internal PowerActions(TaskResults results, Func<int, Task> deny, Action<string> toTray, Func<bool> trayInConsoleSession,
            CapabilityState capabilities, Func<PowerOperation, int, PowerOutcome> platform, Func<bool> hasConsoleUser)
        {
            _results = results ?? throw new ArgumentNullException(nameof(results));
            _deny = deny;
            _toTray = toTray;
            _trayInConsoleSession = trayInConsoleSession;
            _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _hasConsoleUser = hasConsoleUser ?? throw new ArgumentNullException(nameof(hasConsoleUser));
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
            if (!_capabilities.PowerEnabled)
            {
                await _results.SendAsync(taskId, DisabledMessage, CommandRunner.ExitDenied);
                await _deny(taskId);
                return;
            }
            string? error = PowerRequest.Parse(root, taskId, out PowerRequest? request);
            if (request == null)
            {
                POpsHelpers.Log("AGENT", $"[GÜVENLİK] Geçersiz güç isteği reddedildi (TaskID: {taskId}): {error}.", true);
                await _results.SendAsync(taskId, $"[REDDEDİLDİ] Geçersiz güç isteği: {error}.", CommandRunner.ExitDenied);
                return;
            }
            if (request.NeedsUser && !_hasConsoleUser())
            {
                POpsHelpers.Log("AGENT", $"Güç işlemi ({request.Op}) yapılmadı: oturum açık kullanıcı yok (TaskID: {taskId}).");
                await _results.SendAsync(taskId, SessionTasks.NoUserOutput, SessionTasks.ExitNoUser);
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
            Func<TimeSpan, CancellationToken, Task> delay = Delay;
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

        // Başarılı sonucun metni: kapatma ve yeniden başlatmada shutdown.exe'den önce diske yazılır, ötekilerde işlemden sonra
        // gönderilir (kilitleme: tepsi kilitledi; servisin ayırdığı oturum DisconnectedOutput)
        public static string DoneOutput(PowerOperation operation) => operation switch
        {
            PowerOperation.Shutdown => "[TAMAM] Bilgisayar kapatılıyor.",
            PowerOperation.Restart => "[TAMAM] Bilgisayar yeniden başlatılıyor.",
            PowerOperation.Logoff => "[TAMAM] Kullanıcının oturumu kapatılıyor.",
            _ => LockedOutput,
        };

        // Başarısız işlemin sonuç metni; error: PowerOutcome.Error (kapatmada "shutdown.exe <kod>: ...")
        public static string FailedOutput(PowerOperation operation, string error) => operation switch
        {
            PowerOperation.Shutdown => $"[HATA] {error}. Bilgisayar kapatılmadı.",
            PowerOperation.Restart => $"[HATA] {error}. Bilgisayar yeniden başlatılmadı.",
            PowerOperation.Logoff => $"[HATA] Kullanıcının oturumu kapatılamadı: {error}.",
            _ => $"[HATA] Bilgisayar kilitlenemedi: {error}.",
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
                        await _results.SendAsync(taskId, $"[İPTAL EDİLDİ]: Güç işlemi {pending.CancelReason}; bilgisayara dokunulmadı.", CommandRunner.ExitCancelled);
                    else
                        await _results.SendAsync(taskId, "[DURDURULDU]: POps Agent servisi durduğu için güç işlemi yapılmadı.", CommandRunner.ExitServiceStopping);
                    return;
                }
                // Geri sayımda kullanıcı oturumu kapatmış olabilir
                if (request.NeedsUser && !_hasConsoleUser())
                {
                    _toTray(CancelPrefix + taskId.ToString(CultureInfo.InvariantCulture));
                    POpsHelpers.Log("AGENT", $"Güç işlemi ({request.Op}) yapılmadı: süre dolduğunda oturum açık kullanıcı yoktu (TaskID: {taskId}).");
                    await _results.SendAsync(taskId, SessionTasks.NoUserOutput, SessionTasks.ExitNoUser);
                    return;
                }

                if (request.Operation == PowerOperation.Shutdown || request.Operation == PowerOperation.Restart) await ShutDownAsync(request);
                else await SignOutOrLockAsync(pending);
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

        // Kapatma ve yeniden başlatma bağlantıyı keser: sonuç önce gönderilmemiş olarak diske yazılır (gönderilmeye çalışılmaz),
        // sonra shutdown.exe'nin çıkış kodu beklenir. 0: sonuç gönderilmeye çalışılır (gönderilemezse yeniden açılışta
        // diskten gider). Değilse diskteki sonuç hatayla değiştirilip hemen gönderilir; bilgisayar açık kalır.
        private async Task ShutDownAsync(PowerRequest request)
        {
            int taskId = request.TaskId;
            string done = DoneOutput(request.Operation);
            _results.Store(taskId, done, 0);
            PowerOutcome outcome = RunPlatform(request);
            if (outcome.Succeeded)
            {
                POpsHelpers.Log("AGENT", $"Güç işlemi başlatıldı: {request.Op} (TaskID: {taskId}).");
                await _results.SendStoredAsync(taskId, done, 0);
                return;
            }
            POpsHelpers.Log("AGENT", $"[HATA] Güç işlemi yapılamadı: {request.Op} (TaskID: {taskId}): {outcome.Error}; bilgisayar açık kalıyor.", true);
            await _results.SendStoredAsync(taskId, FailedOutput(request.Operation, outcome.Error), outcome.Code);
        }

        // Oturum kapatma ve kilitleme bağlantıyı kesmez: önce işlem, sonra onun sonucu
        private async Task SignOutOrLockAsync(Pending pending)
        {
            PowerRequest request = pending.Request;
            int taskId = request.TaskId;
            string? trayProblem = null;
            if (request.Operation == PowerOperation.Lock && _trayInConsoleSession())
            {
                trayProblem = await LockThroughTrayAsync(pending);
                if (trayProblem == null)
                {
                    POpsHelpers.Log("AGENT", $"Güç işlemi uygulandı: lock, tepsi ekranı kilitledi (TaskID: {taskId}).");
                    await _results.SendAsync(taskId, LockedOutput, 0);
                    return;
                }
                POpsHelpers.Log("AGENT", $"Tepsi kilitlemeyi onaylamadı ({trayProblem}); konsol oturumu servisten ayrılıyor (TaskID: {taskId}).", true);
            }
            PowerOutcome outcome = RunPlatform(request);
            if (outcome.Succeeded)
            {
                POpsHelpers.Log("AGENT", $"Güç işlemi uygulandı: {request.Op} (TaskID: {taskId}).");
                await _results.SendAsync(taskId, request.Operation == PowerOperation.Lock ? DisconnectedOutput : DoneOutput(request.Operation), 0);
                return;
            }
            string error = trayProblem == null ? outcome.Error : $"{trayProblem}; {outcome.Error}";
            POpsHelpers.Log("AGENT", $"[HATA] Güç işlemi yapılamadı: {request.Op} (TaskID: {taskId}): {error}.", true);
            await _results.SendAsync(taskId, FailedOutput(request.Operation, error), outcome.Code);
        }

        // Tepsi kendi oturumunda LockWorkStation'ı çağırır ve "POWER_LOCK_RESULT:<id>:1|0" ile yanıtlar. Dönen: null tepsi
        // kilitledi ("1", TrayLockTimeout içinde); değilse neden (tepsi "0" dedi ya da süre içinde yanıt vermedi)
        private async Task<string?> LockThroughTrayAsync(Pending pending)
        {
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) pending.TrayLock = reply;
            _toTray(LockPrefix + pending.Request.TaskId.ToString(CultureInfo.InvariantCulture));
            Task finished = await Task.WhenAny(reply.Task, Task.Delay(TrayLockTimeout));
            if (finished != reply.Task) return $"tepsi {TrayLockTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} sn içinde yanıt vermedi";
            return await reply.Task ? null : "tepside LockWorkStation başarısız";
        }

        // Güç işleminin kendisi; makine kapısı hata atarsa işlem denenememiş sayılır (-3)
        private PowerOutcome RunPlatform(PowerRequest request)
        {
            try { return _platform(request.Operation, request.TaskId); }
            catch (Exception ex)
            {
                return PowerOutcome.Failed(CommandRunner.ExitAgentError, $"güç işlemi yürütülemedi: {ex.Message.Trim().TrimEnd('.')}");
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

    // Güç işleminin gerçek sonucu (makine kapısı MachineSeams.Power; servis: WindowsPower). Code 0: yapıldı (kapatma ve
    // yeniden başlatmada shutdown.exe kapatmayı başlattı). Değilse sonucun exit_code'u: kilitleme ve oturum kapatmada Win32
    // hata kodu, kapatma ve yeniden başlatmada shutdown.exe'nin çıkış kodu (ya da başlatılamadıysa Win32 hata kodu), işlem
    // hiç denenemediyse CommandRunner.ExitAgentError (-3). Error: başarısızlığın Türkçe açıklaması ("[HATA] ..." metninin
    // gövdesi; başarıda boş).
    [SupportedOSPlatform("windows")]
    public readonly record struct PowerOutcome
    {
        private readonly string? _error;

        private PowerOutcome(int code, string? error)
        {
            Code = code;
            _error = error;
        }

        public int Code { get; }
        public string Error => _error ?? "";
        public bool Succeeded => Code == 0;

        public static PowerOutcome Done => default;

        // Kodu bilinmeyen (0 olan) başarısızlık ajan hatası sayılır: başarısız işlem hiçbir zaman 0 ile bildirilmez
        public static PowerOutcome Failed(int code, string error) => new PowerOutcome(code == 0 ? CommandRunner.ExitAgentError : code, error);
    }

    // Gerçek güç işlemleri. Yalnızca LocalSystem'de (servis) çalışır: elle ya da test sürecinde çalışan kod bilgisayarı
    // kapatamaz. Kapatma ve yeniden başlatma shutdown.exe ile (System32'den, tam yol): açık programlar beklenmeden
    // kapatılır (/f; uyarı geri sayımdır), Olay Günlüğüne "planlı" nedeniyle yazılır. Sonucu yorumlayan parçalar
    // (ShutdownExe, OnConsoleSession) çağrıyı argüman olarak alır: testler sahte süreç ve sahte oturum çağrısıyla sınar.
    [SupportedOSPlatform("windows")]
    internal static class WindowsPower
    {
        // shutdown.exe kapatmayı başlatıp hemen döner; bundan uzun sürerse başarısız sayılır
        internal const int ShutdownExeWaitSeconds = 15;

        public static PowerOutcome Run(PowerOperation operation, int taskId)
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (!identity.IsSystem) return PowerOutcome.Failed(CommandRunner.ExitAgentError, "ajan LocalSystem olarak çalışmıyor");
            }
            switch (operation)
            {
                case PowerOperation.Shutdown: return ShutdownExe(ShutdownStart(restart: false, taskId), RunAndWait);
                case PowerOperation.Restart: return ShutdownExe(ShutdownStart(restart: true, taskId), RunAndWait);
                case PowerOperation.Logoff:
                    return OnConsoleSession(() => UserSessionLauncher.ActiveConsoleSession(),
                        session => (WTSLogoffSession(IntPtr.Zero, session, false), Marshal.GetLastWin32Error()), "WTSLogoffSession");
                default:
                    return OnConsoleSession(() => UserSessionLauncher.ActiveConsoleSession(),
                        session => (WTSDisconnectSession(IntPtr.Zero, session, false), Marshal.GetLastWin32Error()), "WTSDisconnectSession");
            }
        }

        // shutdown.exe /s|/r /t 0 /f /d p:0:0 /c "POps: yönetici isteği (görev N)"
        internal static ProcessStartInfo ShutdownStart(bool restart, int taskId)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(restart ? "/r" : "/s");
            start.ArgumentList.Add("/t");
            start.ArgumentList.Add("0");
            start.ArgumentList.Add("/f");
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("p:0:0");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add($"POps: yönetici isteği (görev {taskId.ToString(CultureInfo.InvariantCulture)})");
            return start;
        }

        // shutdown.exe'nin sonucu. run: süreci başlatır ve çıkış kodunu bekler (null: ShutdownExeWaitSeconds içinde bitmedi;
        // başlatılamazsa Win32Exception ya da InvalidOperationException)
        internal static PowerOutcome ShutdownExe(ProcessStartInfo start, Func<ProcessStartInfo, int?> run)
        {
            int? exitCode;
            try { exitCode = run(start); }
            catch (Win32Exception ex) { return PowerOutcome.Failed(ex.NativeErrorCode, $"shutdown.exe başlatılamadı (Win32 {ex.NativeErrorCode}: {Describe(ex.Message)})"); }
            catch (InvalidOperationException ex) { return PowerOutcome.Failed(CommandRunner.ExitAgentError, $"shutdown.exe başlatılamadı ({Describe(ex.Message)})"); }
            if (exitCode == null) return PowerOutcome.Failed(CommandRunner.ExitAgentError, $"shutdown.exe {ShutdownExeWaitSeconds} sn içinde bitmedi");
            if (exitCode == 0) return PowerOutcome.Done;
            return PowerOutcome.Failed(exitCode.Value, $"shutdown.exe {exitCode.Value.ToString(CultureInfo.InvariantCulture)}: {Win32Message(exitCode.Value)}");
        }

        // Konsol oturumundaki çağrı (WTSLogoffSession, WTSDisconnectSession). call: çağrının dönüşü ve hemen ardından okunan
        // Win32 hata kodu. Etkin konsol oturumu yoksa çağrı yapılmaz (-3).
        internal static PowerOutcome OnConsoleSession(Func<uint> consoleSession, Func<uint, (bool Ok, int Error)> call, string name)
        {
            uint session = consoleSession();
            if (session == UserSessionLauncher.NoSession) return PowerOutcome.Failed(CommandRunner.ExitAgentError, $"{name} çağrılmadı: etkin konsol oturumu yok");
            (bool ok, int error) = call(session);
            if (ok) return PowerOutcome.Done;
            return PowerOutcome.Failed(error, $"{name} başarısız (oturum {session.ToString(CultureInfo.InvariantCulture)}, Win32 {error.ToString(CultureInfo.InvariantCulture)}: {Win32Message(error)})");
        }

        // Win32 hata kodunun sistem iletisi (Türkçe Windows'ta Türkçe), sondaki nokta olmadan
        internal static string Win32Message(int code) => Describe(new Win32Exception(code).Message);

        private static string Describe(string message) => message.Trim().TrimEnd('.');

        private static int? RunAndWait(ProcessStartInfo start)
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("süreç başlatılamadı");
            return process.WaitForExit(ShutdownExeWaitSeconds * 1000) ? process.ExitCode : null;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSLogoffSession(IntPtr server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSDisconnectSession(IntPtr server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);
    }
}
