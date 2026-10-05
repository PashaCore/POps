#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // {"action":"user_message","task_id":n,"title":"1..80","text":"1..1000","style":"info"|"warning","requires_ack":bool,"requested_by":"..."}
    // Sunucunun şeması (docs/protocol/server-to-agent/user_message.json): title, text, style ve requires_ack zorunlu.
    // Sunucu doğrulamış olsa da her alan burada yeniden denetlenir; temizlikten sonra boş kalan başlık ya da metin de
    // geçersizdir (sunucu da kabul etmez).
    public sealed class UserMessageRequest
    {
        public const int MaxTitleLength = 80;
        public const int MaxTextLength = 1000;

        public int TaskId { get; private init; }
        // Kontrol karakterleri atılmış başlık (tek satır) ve metin (satır sonları "\n" olarak kalır)
        public string Title { get; private init; } = "";
        public string Text { get; private init; } = "";
        public string Style { get; private init; } = "info";
        public bool RequiresAck { get; private init; }
        public string? RequestedBy { get; private init; }

        // Dönen: hata nedeni (Türkçe) ya da null; geçerliyse request dolu
        public static string? Parse(JsonElement root, int taskId, out UserMessageRequest? request)
        {
            request = null;
            var (titleValid, title) = SessionTasks.RequiredString(root, "title");
            if (!titleValid || title == null || title.Length == 0 || SessionTasks.Length(title) > MaxTitleLength)
                return $"title 1 ile {MaxTitleLength} karakter arasında bir metin olmalı";
            string cleanTitle = SessionTasks.Clean(title, keepLineBreaks: false);
            if (cleanTitle.Length == 0) return "title boş";

            var (textValid, text) = SessionTasks.RequiredString(root, "text");
            if (!textValid || text == null || text.Length == 0 || SessionTasks.Length(text) > MaxTextLength)
                return $"text 1 ile {MaxTextLength} karakter arasında bir metin olmalı";
            string cleanText = SessionTasks.Clean(text, keepLineBreaks: true);
            if (cleanText.Length == 0) return "text boş";

            string? style = SessionTasks.RequiredString(root, "style").Value;
            if (style != "info" && style != "warning")
                return "style info ya da warning olmalı";

            if (!root.TryGetProperty("requires_ack", out JsonElement ack) || (ack.ValueKind != JsonValueKind.True && ack.ValueKind != JsonValueKind.False))
                return "requires_ack true ya da false olmalı";

            request = new UserMessageRequest
            {
                TaskId = taskId,
                Title = cleanTitle,
                Text = cleanText,
                Style = style,
                RequiresAck = ack.ValueKind == JsonValueKind.True,
                RequestedBy = SessionTasks.RequestedBy(root),
            };
            return null;
        }
    }

    // Kullanıcıya mesaj: tepsi başlık, metin ve simgeyle (info/warning) üstte bir pencere gösterir.
    //  * Sıra: task_id -> yetenek (message_enabled; kapalıysa -5 + capability_denied) -> alanlar (geçersizse -5, yalnızca
    //    result) -> tepsi bağlı mı (değilse ve konsolda kullanıcı yoksa -6 "oturum açık kullanıcı yok"; kullanıcı varsa
    //    -3 "[HATA] ... tepsi çalışmıyor": -6 sunucuda yalnızca "kimse oturum açmamış" demektir; mesaj sıraya alınmaz)
    //    -> gösterilir (Olay Günlüğü 1140, yalnızca üst veri).
    //  * requires_ack yoksa sonuç hemen: "[TAMAM] gösterildi". Varsa sonuç tek ve sonra gelir: kullanıcı Tamam'a basınca
    //    "[TAMAM] okundu", 30 dakikada basılmazsa (ya da servis durursa) "[TAMAM] gösterildi, onaylanmadı" (1141).
    //    Beklerken tepsi yeniden bağlanırsa mesaj yeniden gösterilir; aynı task_id yeniden gelirse yok sayılır;
    //    cancel_task pencereyi kapatır (-2).
    //  * Mesaj metni ve başlığı loglanmaz, Olay Günlüğüne yazılmaz.
    [SupportedOSPlatform("windows")]
    public sealed class UserMessages
    {
        public const string Capability = "message";
        public const string ActionName = "user_message";
        public const string ShownOutput = "[TAMAM] gösterildi";
        public const string ReadOutput = "[TAMAM] okundu";
        public const string NotAcknowledgedOutput = "[TAMAM] gösterildi, onaylanmadı";
        public const string CancelledOutput = "[İPTAL EDİLDİ]: Mesaj panelden geri çekildi; pencere kapatıldı.";
        public const string DisabledMessage = "[REDDEDİLDİ] Bu cihazda kullanıcı mesajları kapalı (yetenek politikası); mesaj gösterilmedi.";
        // Konsolda kullanıcı var ama tepsi bağlı değil: ajan hatası (-3), sunucuda Failed
        public const string NoTrayOutput = "[HATA]: POps tepsisi bu bilgisayarda çalışmıyor; mesaj gösterilmedi.";
        // Boru mesajları: servis -> tepsi
        public const string ShowPrefix = "USER_MESSAGE:";
        public const string ClosePrefix = "USER_MESSAGE_CLOSE:";
        // tepsi -> servis: "USER_MESSAGE_ACK:<task_id>"
        public const string AckPrefix = "USER_MESSAGE_ACK:";

        // Okundu onayı en çok bu kadar beklenir
        internal static TimeSpan AckTimeout { get; set; } = TimeSpan.FromMinutes(30);

        private readonly Func<int, string, int, Task> _sendResult;
        private readonly Func<int, Task> _deny;
        private readonly Action<string> _toTray;
        private readonly Func<bool> _trayConnected;
        private readonly object _gate = new object();
        // Okundu onayı bekleyenler (task_id); sözlükten çıkaran sonucu gönderir
        private readonly Dictionary<int, Pending> _waiting = new Dictionary<int, Pending>();

        // Olay Günlüğü (testler yakalar)
        internal Action<LocalAuditEvent> Audit { get; set; } = LocalAudit.Write;

        // sendResult(task_id, output, exit_code); deny(task_id): capability_denied message/user_message
        public UserMessages(Func<int, string, int, Task> sendResult, Func<int, Task> deny, Action<string> toTray, Func<bool> trayConnected)
        {
            _sendResult = sendResult;
            _deny = deny;
            _toTray = toTray;
            _trayConnected = trayConnected;
        }

        public bool IsWaiting(int taskId) { lock (_gate) return _waiting.ContainsKey(taskId); }

        public async Task HandleAsync(JsonElement root, CancellationToken serviceStopping)
        {
            int? id = SessionTasks.TaskId(root);
            if (id == null)
            {
                POpsHelpers.Log("AGENT", "user_message yok sayıldı: task_id tamsayı değil (sonuç bildirilemez).", true);
                return;
            }
            int taskId = id.Value;
            if (IsWaiting(taskId))
            {
                POpsHelpers.Log("AGENT", $"Mesaj zaten gösteriliyor; yinelenen emir yok sayıldı (TaskID: {taskId}).");
                return;
            }
            if (!AgentCapabilities.MessageEnabled)
            {
                await _sendResult(taskId, DisabledMessage, CommandRunner.ExitDenied);
                await _deny(taskId);
                return;
            }
            string? error = UserMessageRequest.Parse(root, taskId, out UserMessageRequest? request);
            if (request == null)
            {
                POpsHelpers.Log("AGENT", $"[GÜVENLİK] Geçersiz kullanıcı mesajı reddedildi (TaskID: {taskId}): {error}.", true);
                await _sendResult(taskId, $"[REDDEDİLDİ] Geçersiz mesaj: {error}.", CommandRunner.ExitDenied);
                return;
            }
            // Mesajı yalnızca kullanıcının oturumundaki tepsi gösterebilir
            if (!_trayConnected())
            {
                bool user = SessionTasks.HasConsoleUser();
                POpsHelpers.Log("AGENT", $"Mesaj gösterilmedi: {(user ? "tepsi bağlı değil" : "oturum açık kullanıcı yok")} (TaskID: {taskId}).");
                if (user) await _sendResult(taskId, NoTrayOutput, CommandRunner.ExitAgentError);
                else await _sendResult(taskId, SessionTasks.NoUserOutput, SessionTasks.ExitNoUser);
                return;
            }

            Pending? pending = null;
            if (request.RequiresAck)
            {
                pending = new Pending(request, serviceStopping);
                lock (_gate) _waiting[taskId] = pending;
            }
            // Kayıt gösterimden önce: tepsi hemen onaylarsa 1141, 1140'tan sonra gelir
            Audit(LocalAudit.UserMessageShown(taskId, SessionTasks.Length(request.Title), SessionTasks.Length(request.Text),
                request.Style, request.RequiresAck, request.RequestedBy));
            POpsHelpers.Log("AGENT", $"Kullanıcıya mesaj gösteriliyor ({request.Style}{(request.RequiresAck ? ", okundu onayı bekleniyor" : "")}; TaskID: {taskId}).");
            _toTray(ShowMessage(request));
            if (pending == null)
            {
                await _sendResult(taskId, ShownOutput, 0);
                return;
            }
            // Bekleme girişi ve süre kabul anında alınır: arka plandaki görev sonradan değiştirilen girişi kullanmaz
            Func<TimeSpan, CancellationToken, Task> delay = SessionTasks.Delay;
            TimeSpan timeout = AckTimeout;
            _ = Task.Run(() => WaitForAckAsync(pending, delay, timeout, serviceStopping), CancellationToken.None);
        }

        // Tepsi: kullanıcı Tamam'a bastı ("<task_id>"). Dönen: onay bekleyen bir mesajdı
        public async Task<bool> OnAcknowledgedAsync(string payload)
        {
            if (!int.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out int taskId)) return false;
            if (!TryFinish(taskId))
            {
                POpsHelpers.Log("AGENT", $"Okundu onayı yok sayıldı: bekleyen mesaj yok (TaskID: {taskId}).");
                return false;
            }
            Audit(LocalAudit.UserMessageFinished(taskId, "acknowledged"));
            POpsHelpers.Log("AGENT", $"Kullanıcı mesajı okudu (TaskID: {taskId}).");
            await _sendResult(taskId, ReadOutput, 0);
            return true;
        }

        // cancel_task: okundu onayı beklenen mesaj geri çekilir, tepsi pencereyi kapatır. Dönen: böyle bir mesaj vardı mı
        public async Task<bool> CancelAsync(int taskId)
        {
            if (!TryFinish(taskId)) return false;
            _toTray(ClosePrefix + taskId.ToString(CultureInfo.InvariantCulture));
            Audit(LocalAudit.UserMessageFinished(taskId, "cancelled"));
            POpsHelpers.Log("AGENT", $"Kullanıcı mesajı panelden geri çekildi (TaskID: {taskId}).");
            await _sendResult(taskId, CancelledOutput, CommandRunner.ExitCancelled);
            return true;
        }

        // Tepsi yeniden bağlandı (yeniden başladı, oturum değişti): onay bekleyen mesajlar yeniden gösterilir
        public void SyncTray()
        {
            List<UserMessageRequest> waiting;
            lock (_gate) waiting = _waiting.Values.Select(p => p.Request).ToList();
            foreach (UserMessageRequest request in waiting) _toTray(ShowMessage(request));
        }

        // Tepsiye: "USER_MESSAGE:<base64 JSON {task_id, title, text, style, requires_ack}>"
        internal static string ShowMessage(UserMessageRequest request) =>
            ShowPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                task_id = request.TaskId,
                title = request.Title,
                text = request.Text,
                style = request.Style,
                requires_ack = request.RequiresAck,
            }));

        private async Task WaitForAckAsync(Pending pending, Func<TimeSpan, CancellationToken, Task> delay, TimeSpan timeout, CancellationToken serviceStopping)
        {
            int taskId = pending.Request.TaskId;
            try
            {
                try { await delay(timeout, pending.Token); }
                catch (OperationCanceledException) { }
                // Onay ya da iptal geldiyse sonucu o gönderdi
                if (!TryFinish(taskId, pending)) return;
                bool stopping = serviceStopping.IsCancellationRequested;
                Audit(LocalAudit.UserMessageFinished(taskId, stopping ? "service_stopping" : "timeout"));
                POpsHelpers.Log("AGENT", $"Kullanıcı mesajı {(stopping ? "servis durduğu için" : $"{timeout.TotalMinutes:0} dakikada")} onaylanmadı (TaskID: {taskId}).");
                await _sendResult(taskId, NotAcknowledgedOutput, 0);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"[HATA] Mesaj onayı beklenemedi (TaskID: {taskId}): {ex.Message}", true);
            }
            finally
            {
                pending.Dispose();
            }
        }

        // Bekleyen mesajı sözlükten çıkaran sonucu gönderir; bekleme (süre) durdurulur
        private bool TryFinish(int taskId, Pending? expected = null)
        {
            Pending? pending;
            lock (_gate)
            {
                if (!_waiting.TryGetValue(taskId, out pending) || (expected != null && pending != expected)) return false;
                _waiting.Remove(taskId);
            }
            pending.Stop();
            return true;
        }

        private sealed class Pending : IDisposable
        {
            private readonly CancellationTokenSource _cts;

            public Pending(UserMessageRequest request, CancellationToken serviceStopping)
            {
                Request = request;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping);
            }

            public UserMessageRequest Request { get; }
            public CancellationToken Token => _cts.Token;

            public void Stop()
            {
                try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            }

            public void Dispose() => _cts.Dispose();
        }
    }
}
