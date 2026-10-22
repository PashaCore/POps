using System;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Tepsi borusu (servis boyunca tek; bkz. TrayPipeServer) ve tepsiden gelenlerin yönlendirilmesi. Metin mesajı sıralı
    // (eşleşme, işleyici) listesinde ilk eşleşen işleyiciye gider, eşleşmeyen yok sayılır; sıra eski else-if zincirinin
    // sırasıdır (START_VISION_TUNNEL bir ön ek eşleşmesidir). JPEG kare önce bekleyen ekran önizlemesine, Vision v2 karesi
    // Vision oturumuna gider. Tepsinin bağlanma ve kopma olayları burada bir arada.
    [SupportedOSPlatform("windows")]
    internal sealed class TrayMessageRouter
    {
        private readonly Func<TrayPipeServer> _newPipe;
        private readonly AgentStartupHealth _startupHealth;
        private readonly VisionSession _vision;
        private readonly RemoteInputHandler _remoteInput;
        private readonly CommandChannel _channel;
        private readonly Func<string?> _hwId;
        private readonly PolicySync _policy;
        private readonly Helpdesk _helpdesk;
        private readonly ActivityHistory _activity;
        private readonly PowerActions _power;
        private readonly UserMessages _messages;
        private readonly Func<QuarantineControl> _quarantine;
        private readonly Func<string, Task> _bypassAttempt;
        private readonly Action<string> _toTray;
        private readonly Func<string?> _configErrorMessage;
        // Tepsinin metin mesajları: sırayla denenir, ilk eşleşen işler (bkz. Route)
        private readonly (Func<string, bool> Matches, Action<string> Handle)[] _routes;

        private TrayPipeServer? _trayPipe;
        // Ön plandaki uygulamanın süreç adı (tepsiden, yalnızca ad; bkz. ActiveApp). Bilinmiyorsa null.
        private volatile string? _activeApp;

        // newPipe: boruyu kurar (bir kez; boru servis boyunca açık kalır, Worker atılırken de kapatılmaz). Kimlik ve
        // karantina denetimi çalışırken değişebilir (set_identity, testler): her kullanımda okunur. bypassAttempt: çevrimdışı
        // bypass kodu (UNLOCK_BYPASS:); toTray: Worker.ToTray (testlerde TrayOverride); configErrorMessage: yapılandırma
        // sorunu (yoksa null; bkz. Worker.ConfigErrorMessage)
        public TrayMessageRouter(Func<TrayPipeServer> newPipe, AgentStartupHealth startupHealth, VisionSession vision,
            RemoteInputHandler remoteInput, CommandChannel channel, Func<string?> hwId, PolicySync policy, Helpdesk helpdesk,
            ActivityHistory activity, PowerActions power, UserMessages messages, Func<QuarantineControl> quarantine,
            Func<string, Task> bypassAttempt, Action<string> toTray, Func<string?> configErrorMessage)
        {
            _newPipe = newPipe ?? throw new ArgumentNullException(nameof(newPipe));
            _startupHealth = startupHealth ?? throw new ArgumentNullException(nameof(startupHealth));
            _vision = vision ?? throw new ArgumentNullException(nameof(vision));
            _remoteInput = remoteInput ?? throw new ArgumentNullException(nameof(remoteInput));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _helpdesk = helpdesk ?? throw new ArgumentNullException(nameof(helpdesk));
            _activity = activity ?? throw new ArgumentNullException(nameof(activity));
            _power = power ?? throw new ArgumentNullException(nameof(power));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
            _bypassAttempt = bypassAttempt ?? throw new ArgumentNullException(nameof(bypassAttempt));
            _toTray = toTray ?? throw new ArgumentNullException(nameof(toTray));
            _configErrorMessage = configErrorMessage ?? throw new ArgumentNullException(nameof(configErrorMessage));
            _routes = new (Func<string, bool>, Action<string>)[]
            {
                // Eski tepsi sürümlerinin "WatchDog'u / ekran izlemeyi duraklat" komutları artık kabul edilmez
                Prefix("USER_COMMAND:", message => POpsHelpers.Log("AGENT", $"Yok sayılan kullanıcı komutu: {message}")),
                Exact("FAIR_USE_ACK", message =>
                {
                    _policy.AcknowledgeFairUse();
                    POpsHelpers.Log("AGENT", "Kullanıcı aydınlatma metnini onayladı.");
                }),
                // Yalnızca süreç adı; heartbeat'te active_window olarak gider
                Prefix("ACTIVE_APP:", message =>
                {
                    string? app = ActiveApp.Sanitize(message.Substring("ACTIVE_APP:".Length));
                    if (app != null) _activeApp = app;
                }),
                // Eski tepsi pencere başlığı gönderir: KVKK gereği sunucuya iletilmez (bkz. ActiveApp)
                Prefix("ACTIVE_WINDOW:", message => { }),
                Prefix(VisionSessionStart.TunnelPrefix, message => _ = Task.Run(() => _vision.StartVisionFromTrayAsync(message))),
                Prefix("REJECT_VISION_TUNNEL:", RejectVisionTunnel),
                Exact("STOP_VISION_TUNNEL", message =>
                {
                    _vision.RevokeApproval();
                    _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
                    _ = _vision.DisconnectVisionTunnelAsync();
                }),
                Prefix("UNLOCK_BYPASS:", message => _ = _bypassAttempt(message.Substring("UNLOCK_BYPASS:".Length))),
                Prefix("VISION_MONITORS:", message =>
                {
                    if (_vision.StreamActive && _vision.BinaryVision) _ = _vision.Relay.ForwardMonitorsAsync(message.Substring("VISION_MONITORS:".Length));
                }),
                Prefix("CLIPBOARD:", message => _ = _vision.Relay.ForwardClipboardAsync(message.Substring("CLIPBOARD:".Length), _vision.ClipboardAllowed)),
                Prefix("TICKET_CREATE:", message => _ = _helpdesk.CreateAsync(message.Substring("TICKET_CREATE:".Length))),
                Exact("TICKET_LIST", message => _ = _helpdesk.ListAsync()),
                Exact("ACTIVITY_LIST", message => _ = _activity.ListAsync()),
                (message => message.StartsWith(UserMessages.AckPrefix, StringComparison.Ordinal) || message.StartsWith(PowerActions.LockResultPrefix, StringComparison.Ordinal),
                    message => _ = OnTrayReplyAsync(message)),
            };
        }

        private static (Func<string, bool>, Action<string>) Prefix(string prefix, Action<string> handle) =>
            (message => message.StartsWith(prefix, StringComparison.Ordinal), handle);

        private static (Func<string, bool>, Action<string>) Exact(string text, Action<string> handle) =>
            (message => message == text, handle);

        // Servis boyunca tek boru (bkz. EnsureTrayPipeServer); kurulana kadar null
        internal TrayPipeServer? Pipe => _trayPipe;

        // Heartbeat'in active_window'u
        internal string? ForegroundApp => _activeApp;

        // Konsoldaki kullanıcı değişti: öncekinin uygulaması heartbeat'te kalmaz
        internal void ForgetForegroundApp() => _activeApp = null;

        // Boru dinlemeye geçince sağlık kontrolü işaretlenir (bkz. AgentStartupHealth). Bağlantı döngüsü bunu
        // beklemez: boru adı başka bir süreçte kalırsa (ör. yerel bir kullanıcı adı önceden aldıysa) tepsi çalışmaz
        // ama ajan sunucuya yine bağlanır; health.json yazılmadığı için güncelleme de başarılı sayılmaz.
        // Servis boyunca tek boru: zaten açıksa bir şey yapılmaz. Dinleme döngüsü hata ve tepsi kopmalarında kendini yeniler.
        internal void EnsureTrayPipeServer()
        {
            if (_trayPipe != null) return;
            _trayPipe = _newPipe();

            _trayPipe.OnMessageReceived += Route;

            // JPEG kare: önce bekleyen ekran önizlemesi (RemoteInputHandler), yoksa eski (JSON) yayının karesi (VisionSession)
            _trayPipe.OnFrameReceived += async (jpegBytes) =>
            {
                if (_remoteInput.TryCompleteSnapshot(jpegBytes)) return;
                await _vision.ForwardStreamFrameAsync(jpegBytes);
            };

            // Vision v2 karesi (işaretli): yalnızca yayın açıkken ve sunucu ikili kareyi destekliyorsa
            _trayPipe.OnVisionFrame += data =>
            {
                if (_vision.StreamActive && _vision.BinaryVision) _ = _vision.Relay.ForwardFrameAsync(data);
            };

            _trayPipe.OnDisconnected += () =>
            {
                _vision.RevokeApproval();
                _activeApp = null;
            };

            // Kilit ekranı tepsiyle birlikte kapanmış olabilir: karantina sürüyorsa yeniden gösterilir
            _trayPipe.OnConnected += () =>
            {
                _quarantine().SyncTray();
                if (ExamMode.IsActive) _toTray(ExamHandler.ExamTrayMessage(ExamMode.Load()));
                _policy.SyncTrayModules();
                string? configError = _configErrorMessage();
                if (configError != null) _trayPipe?.SendCommandToDesktop(configError);
                // Süren geri sayım ve okundu onayı bekleyen mesajlar yeniden gösterilir
                _power.SyncTray();
                _messages.SyncTray();
            };

            _trayPipe.Start().ContinueWith(_ => _startupHealth.Mark(StartupCheck.Pipe), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        // Tepsinin metin mesajı: listede ilk eşleşenin işleyicisi çalışır; hiçbiri eşleşmezse yok sayılır
        internal void Route(string message)
        {
            foreach (var (matches, handle) in _routes)
            {
                if (!matches(message)) continue;
                handle(message);
                return;
            }
        }

        // Tepsi Vision oturumunu reddetti: sunucuya vision_rejected (o anki komut soketine, kilit en çok 2 sn beklenir)
        private void RejectVisionTunnel(string message)
        {
            _vision.RevokeApproval();
            string sessionId = message.Split(':')[1];
            var payload = new { type = "vision_rejected", session_id = sessionId, hw_id = _hwId() };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            _ = Task.Run(() => _channel.SendOnAsync(() => _channel.Socket, bytes, 2000));
        }

        // Tepsinin güç/mesaj yanıtları: "USER_MESSAGE_ACK:<task_id>" (kullanıcı Tamam'a bastı),
        // "POWER_LOCK_RESULT:<task_id>:1|0" (LockWorkStation). Dönen: bekleyen bir göreve aitti.
        internal async Task<bool> OnTrayReplyAsync(string message)
        {
            try
            {
                if (message.StartsWith(UserMessages.AckPrefix, StringComparison.Ordinal))
                    return await _messages.OnAcknowledgedAsync(message.Substring(UserMessages.AckPrefix.Length));
                if (message.StartsWith(PowerActions.LockResultPrefix, StringComparison.Ordinal))
                    return _power.OnTrayLockResult(message.Substring(PowerActions.LockResultPrefix.Length));
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Tepsi yanıtı işlenemedi: {ex.Message}", true); }
            return false;
        }
    }
}
