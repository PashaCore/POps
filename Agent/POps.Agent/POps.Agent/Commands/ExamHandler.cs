using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // exam_mode: sunucunun sınav modu emri (bkz. ExamMode). Giriş ve çıkış, exam_state ve dönemsel tur (süre, engelli
    // uygulamalar, izin listesi) burada; turu 2 sn'de bir Worker'ın sınav döngüsü çağırır. Worker atılınca atılır.
    [SupportedOSPlatform("windows")]
    internal sealed class ExamHandler : ICommandHandler, IDisposable
    {
        private readonly CapabilityGate _gate;
        private readonly ServerHandshake _handshake;
        private readonly Func<object, Task<bool>> _send;
        private readonly Action<string> _toTray;
        private readonly Action<LocalAuditEvent> _audit;
        private readonly string _serverUrl;
        private readonly Func<DateTimeOffset> _clock;

        // send: komut kanalına gönderim; clock: emrin doğrulandığı ve sınavdan çıkılan an (testlerde sahteleri)
        public ExamHandler(CapabilityGate gate, ServerHandshake handshake, Func<object, Task<bool>> send, Action<string> toTray,
            Action<LocalAuditEvent> audit, string serverUrl, Func<DateTimeOffset> clock)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _toTray = toTray ?? throw new ArgumentNullException(nameof(toTray));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _serverUrl = serverUrl;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "exam_mode" };

        public Task HandleAsync(ServerCommand command) => HandleExamModeAsync(command.Root);

        public void Dispose() => _examGate.Dispose();

        private readonly HashSet<string> _examStoppedLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tepsi bandı: mesaj ve bitiş zamanı
        internal static string ExamTrayMessage(ExamSettings? settings) =>
            "EXAM_ON:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["message"] = string.IsNullOrWhiteSpace(settings?.Message) ? "Sınav modu: yalnızca izin verilen siteler açık" : settings.Message,
                ["until"] = settings?.Until,
            }));

        // Sınava giriş ve çıkış birbirini beklemez olmasın (dönemsel tur, sunucu emri, set_capabilities aynı anda gelebilir)
        private readonly SemaphoreSlim _examGate = new SemaphoreSlim(1, 1);
        // Bu çalışmada sınav modundan çıkılan an (unix sn; 0: bu çalışmada çıkılmadı). exam_state.since için bellekte tutulur.
        private long _examLeftAt;
        // Bu bağlantıda bağlantı sonrası exam_state gönderildi mi (server_info'dan sonra bir kez; bkz. ReportExamStateOnConnectAsync)
        private volatile bool _examStateReported;

        internal async Task HandleExamModeAsync(JsonElement root)
        {
            bool enable = root.TryGetProperty("enabled", out JsonElement e) && e.ValueKind == JsonValueKind.True;
            if (!enable)
            {
                await EndExamAsync("server", reply: true);
                return;
            }
            if (!AgentCapabilities.ExamEnabled)
            {
                // Yerel olarak kapalı: hiçbir şey uygulanmaz; önceden kalan bir sınav sürüyorsa biter (exam_state ile)
                await _gate.DenyAsync("exam", "exam_mode");
                if (ExamMode.IsActive) await EndExamAsync("capability", reply: true);
                return;
            }
            if (!ExamMode.TryParse(root, _clock(), out ExamSettings? settings, out string? error))
            {
                POpsHelpers.Log("EXAM", $"Sınav modu emri uygulanmadı: {error}.", true);
                await SendExamStateAsync(reply: true);
                return;
            }
            await _examGate.WaitAsync();
            try { await EnterExamAsync(settings); }
            finally { _examGate.Release(); }
            await SendExamStateAsync(reply: true);
        }

        private async Task EnterExamAsync(ExamSettings settings)
        {
            // Uygulanamazsa neden ExamMode'da yerel loga yazılır; sunucuya o anki durum gider
            if (!await ExamMode.EnableAsync(settings, _serverUrl)) return;
            // Sınav modunda eş önbelleği sunulmaz; arka planda yeniden denetlenir (bkz. PeerCache)
            _ = Task.Run(PeerCache.SyncAsync);
            lock (_examStoppedLogged) _examStoppedLogged.Clear();
            _audit(LocalAudit.ExamStarted(settings));
            POpsHelpers.Log("EXAM", $"SINAV MODU AKTİF: {settings.Allow.Count} izinli kayıt, bitiş {(settings.Until == null ? "yok" : DateTimeOffset.FromUnixTimeSeconds(settings.Until.Value).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))}, {settings.BlockApps.Count} engelli uygulama.");
            _toTray(ExamTrayMessage(ExamMode.Load()));
        }

        // source: "server", "until" (süre doldu; sunucuya ulaşılamasa da), "capability" (sınav yeteneği yerel olarak
        // kapalı: MSI EXAM_ENABLED=0 ile yeniden kurulum ya da set_capabilities). reply: exam_mode emrine yanıt.
        internal async Task EndExamAsync(string source, bool reply = false)
        {
            await _examGate.WaitAsync();
            try { await LeaveExamAsync(source); }
            finally { _examGate.Release(); }
            await SendExamStateAsync(reply);
        }

        private async Task LeaveExamAsync(string source)
        {
            // Kaldırılamazsa neden ExamMode'da loglanır; sunucuya "hâlâ sınavda" gider
            if (!ExamMode.IsActive || !await ExamMode.DisableAsync()) return;
            _ = Task.Run(PeerCache.SyncAsync);
            Interlocked.Exchange(ref _examLeftAt, _clock().ToUnixTimeSeconds());
            _audit(LocalAudit.ExamEnded(source));
            POpsHelpers.Log("EXAM", $"Sınav modu bitti ({source}).");
            _toTray("EXAM_OFF");
        }

        // exam_state hiçbir zaman bağlantının ilk mesajı değildir: exam_mode emrine yanıt (reply) emri gönderen sunucuya
        // gider; kendiliğinden değişiklik (until, yetenek kapandı) yalnızca server_info'da exam_mode duyuran sunucuya.
        // server_info henüz gelmediyse gönderilmez: bağlantı sonrası bildirim (ReportExamStateOnConnectAsync) o anki
        // durumu zaten taşır.
        private async Task SendExamStateAsync(bool reply)
        {
            if (!reply && _handshake.Supports(ExamMode.Feature) != true) return;
            long left = Interlocked.Read(ref _examLeftAt);
            await _send(ExamMode.StateMessage(left == 0 ? null : left));
        }

        // Her bağlantıda server_info'dan sonra bir kez (sunucu exam_mode duyurduysa): sunucu sınav durumunu bağlantı
        // başında öğrenir (sınavda değilken de).
        internal async Task ReportExamStateOnConnectAsync()
        {
            if (_examStateReported || _handshake.Supports(ExamMode.Feature) != true) return;
            _examStateReported = true;
            await SendExamStateAsync(reply: false);
        }

        // Yeni komut bağlantısı: exam_state bu bağlantıda server_info'dan sonra yeniden bildirilir
        internal void OnCommandSocketOpened() => _examStateReported = false;

        // Bir tur: sınav yeteneği yerel olarak kapalıysa (EXAM_ENABLED=0 ile yeniden kurulum) ya da süre dolduysa
        // (sunucuya ulaşılamasa da) biter; yoksa engelli uygulamalar kapatılır, refreshReason verilmişse izin listesi
        // yeniden çözülür. Dönen: sınav bu turda bitti mi.
        internal async Task<bool> ExamTickAsync(DateTimeOffset now, string? refreshReason)
        {
            ExamSettings? settings = ExamMode.IsActive ? ExamMode.Load() : null;
            if (settings == null) return false;
            bool allowed = AgentCapabilities.ExamEnabled;
            if (!allowed || ExamMode.Expired(settings, now))
            {
                await EndExamAsync(allowed ? "until" : "capability");
                return !ExamMode.IsActive;
            }
            StopBlockedApps(settings);
            if (refreshReason != null) await ExamMode.RefreshAsync(_serverUrl, refreshReason);
            return false;
        }

        private void StopBlockedApps(ExamSettings settings)
        {
            if (settings.BlockApps.Count == 0) return;
            Process[] all = Process.GetProcesses();
            try
            {
                var candidates = all.Select(p =>
                {
                    try { return (Pid: p.Id, Name: p.ProcessName, Session: p.SessionId); }
                    catch (InvalidOperationException) { return (Pid: p.Id, Name: (string?)null, Session: 0); }
                }).ToList();
                foreach (int pid in ExamMode.ProcessesToStop(candidates, settings.BlockApps))
                {
                    Process process = all.First(p => p.Id == pid);
                    string app = candidates.First(c => c.Pid == pid).Name + ".exe";
                    try
                    {
                        ExamMode.StopProcess(process);
                        bool first;
                        lock (_examStoppedLogged) first = _examStoppedLogged.Add(app);
                        if (first)
                        {
                            _audit(LocalAudit.ExamAppStopped(app, pid));
                            POpsHelpers.Log("EXAM", $"Sınav modunda {app} kapatıldı (PID {pid}).");
                        }
                        _toTray("EXAM_APP_BLOCKED:" + app);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                }
            }
            finally
            {
                foreach (Process p in all) p.Dispose();
            }
        }
    }
}
