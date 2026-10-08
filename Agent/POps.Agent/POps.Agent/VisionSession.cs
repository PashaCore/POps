using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Vision oturumu: tünel (/ws/vision) ve kapanış kodları, onay ve Olay Günlüğü kaydı, kilitliyken başlayan oturumun
    // bildirimi, ekran yakalama, Vision v2 (bkz. VisionRelay) ve görüntüleyici denetimi. Komutlar VisionHandler ve
    // RemoteInputHandler'dan, tepsi olayları Worker'ın tepsi borusundan gelir; heartbeat, set_capabilities, modül
    // değişikliği ve bağlantı kaybı oturumun durumunu buradan okur.
    [SupportedOSPlatform("windows")]
    internal sealed class VisionSession
    {
        // Mesaj boyu sınırı (parçalı mesajlar EndOfMessage'a kadar birleştirilir; bkz. WebSocketMessages)
        private const int MaxVisionMessageBytes = 1024 * 1024;

        private readonly Func<string?> _hwId;
        private readonly string _pcName;
        private readonly string _serverUrl;
        private readonly CapabilityGate _gate;
        private readonly ServerHandshake _handshake;
        private readonly Action<string> _toTray;
        private readonly Func<TrayPipeServer?> _trayPipe;
        private readonly Action<LocalAuditEvent> _audit;

        private ClientWebSocket? _visionWs;
        private readonly SemaphoreSlim _wsVisionLock;
        // Vision v2: ikili kareler, görüntüleyici denetimi, pano (bkz. VisionRelay)
        private readonly VisionRelay _visionRelay;

        private volatile bool _isVisionStreamActive;
        // Uzaktan fare/klavye yalnızca kullanıcının tepsi üzerinden onayladığı (ya da zorunlu oturumda bildirimin
        // gösterildiği) Vision oturumu açıkken uygulanır. Sunucu ele geçirilse bile yerel onay olmadan girdi yok.
        private volatile bool _visionSessionApproved;
        private string? _visionSessionId;
        private string? _visionRequestedBy;
        private string? _visionReason;
        private bool _visionUserApproved;
        private bool _visionAuditActive;
        private bool _pendingVisionMandatory;
        // Bu oturumda tepsinin oturum bildirimi kuruldu (oturum bilgisayar kilitliyken başladı; bkz. OnVisionStartedLocked)
        private bool _visionNoticeArmed;

        // Kimlik ve tepsi borusu her kullanımda okunur. toTray: Worker.ToTray (testlerde TrayOverride); trayPipe: doğrudan
        // boruya giden mesajlar; audit: Vision olaylarının Olay Günlüğü kaydı (testlerde AuditOverride); visionLock: Vision
        // tünelinin yazma kilidi (Worker'ındır, servis boyunca yaşar)
        public VisionSession(Func<string?> hwId, string pcName, string serverUrl, CapabilityGate gate, ServerHandshake handshake,
            Action<string> toTray, Func<TrayPipeServer?> trayPipe, Action<LocalAuditEvent> audit, SemaphoreSlim visionLock)
        {
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _pcName = pcName;
            _serverUrl = serverUrl;
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
            _toTray = toTray ?? throw new ArgumentNullException(nameof(toTray));
            _trayPipe = trayPipe ?? throw new ArgumentNullException(nameof(trayPipe));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _wsVisionLock = visionLock ?? throw new ArgumentNullException(nameof(visionLock));
            _visionRelay = new VisionRelay(SendVisionBinaryAsync, SendVisionTextAsync, toTray);
        }

        // Testler: Vision tünelinin açılması (gerçek sunucuya bağlanılmaz; true: tünel açıldı) ve sunucunun olay günlüğüne
        // giden kayıt (POST /api/logs)
        internal Func<Task<bool>>? TunnelOverride { get; set; }
        internal Func<AgentLogPayload, Task>? DeviceLogOverride { get; set; }

        // Oturumun durumu ve Vision v2 aktarımı (heartbeat, set_capabilities, modül değişikliği, bağlantı kaybı, tepsi borusu)
        internal bool StreamActive => _isVisionStreamActive;
        internal bool SessionApproved => _visionSessionApproved;
        internal bool HasTunnel => _visionWs != null;
        internal bool TunnelOpen => _visionWs?.State == WebSocketState.Open;
        internal VisionRelay Relay => _visionRelay;

        // Tepsi oturumu reddetti ya da kapattı, tepsi koptu, modül kapandı, bağlantı koptu: uzaktan girdi biter
        internal void RevokeApproval() => _visionSessionApproved = false;

        // Tepsi oturumu başlattı (kullanıcı kabul etti ya da zorunlu oturumun geri sayımı bitti). Tünel açılmadıysa (Vision
        // kapalı, şifresiz sunucu, bağlantı hatası) ekran yakalanmaz. İstek doğrulanmış tepsiden, kullanıcı onayından sonra
        // geldiği için oturum onaylıdır.
        internal async Task StartVisionFromTrayAsync(string message)
        {
            (int fps, bool lockedAtStart) = VisionSessionStart.ParseTunnelMessage(message);
            await ConnectVisionTunnelAsync(CancellationToken.None);
            if (!_isVisionStreamActive) return;
            _visionSessionApproved = true;
            _visionUserApproved = !_pendingVisionMandatory;
            if (!_visionAuditActive)
            {
                _audit(LocalAudit.VisionStarted(_visionSessionId, _visionRequestedBy, _visionUserApproved));
                _visionAuditActive = true;
            }
            // Bildirim yakalamadan önce kurulur: tepsi kullanıcının masaüstünü ancak bildirim ekrandayken yakalar
            if (lockedAtStart) OnVisionStartedLocked();
            StartCapture(fps);
        }

        // Karar: docs/vision.md "Secure desktop", karar 5. Oturum başlarken kullanıcının masaüstü ekranda değildi (kilit,
        // oturum açma, UAC ya da Ctrl+Alt+Del ekranı; geri sayım görünmedi). Sessiz oturum olmasın: Olay Günlüğüne 1150,
        // sunucunun olay günlüğüne kayıt (mevcut POST /api/logs, protokol değişmedi) ve tepside oturum bildirimi; tepsi onu
        // kullanıcının masaüstü geri gelince gösterir, oturum bitene kadar (DisconnectVisionTunnelAsync) açık tutar.
        // Her kilitli başlatma için bir kez (tepsi yeniden bağlanmış olabilir, bildirimi yeniden kurulmalı).
        private void OnVisionStartedLocked()
        {
            bool mandatory = _pendingVisionMandatory;
            POpsHelpers.Log("VISION", "Vision oturumu bilgisayar kilitliyken başladı; kullanıcının masaüstü geri gelince oturum bildirimi gösterilecek.");
            _audit(LocalAudit.VisionStartedWhileLocked(_visionSessionId, _visionRequestedBy, mandatory));
            _visionNoticeArmed = true;
            _toTray(VisionSessionNotice.OnMessage(new VisionNoticeInfo(_visionSessionId, _visionRequestedBy, _visionReason, mandatory)));
            AgentLogPayload log = VisionLockedStartLog(_visionSessionId, _visionRequestedBy, mandatory);
            _ = DeviceLogOverride != null
                ? DeviceLogOverride(log)
                : AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/logs/", _hwId()), _hwId(), log, "Kilitliyken başlayan Vision oturumu kaydı");
        }

        // Sunucuda sıradan bir olay günlüğü kaydı (agent_logs_v2); sunucu bu türe özel bir şey yapmaz
        internal static AgentLogPayload VisionLockedStartLog(string? sessionId, string? requestedBy, bool mandatory) => new AgentLogPayload
        {
            LogType = "Security",
            Message = "Vision oturumu bilgisayar kilitliyken başladı; kullanıcı masaüstüne dönünce oturum bildirimi gösterilir",
            EventType = "agent.vision_locked_start",
            Category = "vision",
            Action = "vision_started_while_locked",
            RiskLevel = "medium",
            MetaData = new Dictionary<string, object>
            {
                ["session_id"] = LogText.Safe(sessionId, 100),
                ["requested_by"] = LogText.Safe(requestedBy, 100),
                ["mandatory"] = mandatory,
            },
        };

        private async Task ConnectVisionTunnelAsync(CancellationToken token)
        {
            if (_visionWs != null && _visionWs.State == WebSocketState.Open) return;
            if (!AgentCapabilities.VisionEnabled)
            {
                await _gate.DenyAsync("vision", "vision_tunnel");
                return;
            }
            if (!_gate.ModuleEnabled(AgentModules.Vision))
            {
                await _gate.DenyAsync("vision", "vision_tunnel", reason: AgentModules.DisabledReason);
                return;
            }
            if (TunnelOverride != null)
            {
                _isVisionStreamActive = await TunnelOverride();
                return;
            }
            // Ekran akışı ve uzaktan girdi yalnızca şifreli kanaldan (bkz. POpsHelpers.IsSecureServerUrl). Tepsi
            // START_VISION_TUNNEL'ı komut tüneli bağlı olmasa da isteyebildiği için burada ayrıca denetlenir.
            if (!POpsHelpers.IsSecureServerUrl(_serverUrl))
            {
                POpsHelpers.Log("AGENT", "[GÜVENLİK] Vision tüneli açılmadı: ServerUrl şifresiz ve yerel değil.", true);
                return;
            }
            string visionWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://") + $"/ws/vision/{_hwId()}";
            string secret = AgentCredentials.CurrentSecret ?? AgentCredentials.LoadSecret();
            VisionAuthSelection auth = VisionChannel.SelectHeaders(secret, AgentCredentials.GetEnrollToken(), POpsHelpers.AppVersion);
            if (!auth.CanConnect)
            {
                POpsHelpers.Log("AGENT", "[GÜVENLİK] Vision tüneli açılmadı: cihaz henüz kayıtlı değil (anahtar yok)", true);
                await _gate.DenyAsync("vision", "vision_tunnel", reason: "not_enrolled");
                return;
            }
            var newWs = new ClientWebSocket();
            newWs.Options.RemoteCertificateValidationCallback = ServerTrust.WebSocketCallback(new Uri(visionWsUrl));
            foreach (KeyValuePair<string, string> header in auth.Headers)
                newWs.Options.SetRequestHeader(header.Key, header.Value);
            try
            {
                await newWs.ConnectAsync(new Uri(visionWsUrl), token);
                var oldWs = Interlocked.Exchange(ref _visionWs, newWs);
                if (oldWs != null && oldWs.State == WebSocketState.Open) { try { await oldWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Değişti", CancellationToken.None); } catch { } oldWs.Dispose(); }
                _isVisionStreamActive = true;
                // Tünel bu arada kapatıldıysa (alan null) dinlenecek bir şey yok
                if (_visionWs != null) _ = ReceiveVisionInputsAsync(_visionWs, token);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"[!] Vision Tüneli açılamadı: {ex.Message}", true);
                ApplyVisionClose(newWs.CloseStatus);
                newWs.Dispose();
            }
        }

        internal async Task DisconnectVisionTunnelAsync()
        {
            _isVisionStreamActive = false;
            _visionSessionApproved = false;
            var ws = Interlocked.Exchange(ref _visionWs, null);
            if (ws != null) { try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Yayın Kesildi", CancellationToken.None); } catch { } ws.Dispose(); }
            if (_visionAuditActive)
            {
                _audit(LocalAudit.VisionFinished(_visionSessionId, _visionRequestedBy, _visionUserApproved));
                _visionAuditActive = false;
            }
            // Oturum bitti: kilitliyken başlayan oturumun bildirimi kapanır (tepsi STOP_CAPTURE'da da kapatır; bu yol
            // sunucunun tüneli kapattığı durumu da kapsar)
            if (_visionNoticeArmed)
            {
                _visionNoticeArmed = false;
                _toTray(VisionSessionNotice.Off);
            }
        }

        // Uzaktan fare/klavye olayı (input_type taşıyan remote_input). Ekran önizlemesi ve FPS ayarı girdi değildir.
        internal static bool IsInputEvent(JsonElement root) => root.TryGetProperty("input_type", out _);

        // Reddedilirse capability_denied'ın yeteneği ve nedeni; yerel yetenek kilidi önce, sonra sunucu modülü, sonra onay
        internal (string? Capability, string? Reason) VisionDenial(bool isInputEvent)
        {
            if (AgentCapabilities.VisionEnabled && !_gate.ModuleEnabled(AgentModules.Vision)) return ("vision", AgentModules.DisabledReason);
            return (VisionInputGate.DenialReason(AgentCapabilities.VisionEnabled, _visionSessionApproved, _isVisionStreamActive, isInputEvent), null);
        }

        private async Task ReceiveVisionInputsAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[8192];
            try
            {
                while (ws.State == WebSocketState.Open && _isVisionStreamActive)
                {
                    var (message, type) = await WebSocketMessages.ReceiveTextAsync(ws, buffer, MaxVisionMessageBytes, token);
                    if (type == WebSocketMessageType.Close) break;
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "remote_input")
                    {
                        string? targetDevice = root.GetProperty("device").GetString();
                        if (targetDevice != _hwId()) continue;
                        var (denial, denialReason) = VisionDenial(IsInputEvent(root));
                        if (denial != null) { await _gate.DenyAsync(denial, "remote_input", reason: denialReason); continue; }
                        _trayPipe()?.SendCommandToDesktop(message);
                    }
                    else if (root.TryGetProperty("action", out _))
                    {
                        await HandleVisionControlAsync(root);
                    }
                }
            }
            catch (WebSocketMessages.TooLargeException ex) { POpsHelpers.Log("AGENT", $"[GÜVENLİK] Vision tüneli kapatıldı: {ex.Message}.", true); }
            catch { }
            finally
            {
                ApplyVisionClose(ws.CloseStatus);
                await DisconnectVisionTunnelAsync();
            }
        }

        // ------------------------------------------------------------------ Vision v2
        // Sunucu ikili Vision karesini destekliyor mu (server_info "vision_binary"); desteklemiyorsa eski JSON kareler
        internal bool BinaryVision => _handshake.Supports(VisionRelay.BinaryFeature) == true;

        // Pano yalnızca kullanıcının kabul ettiği (zorunlu bildirimli değil), açık bir v2 oturumunda
        internal bool ClipboardAllowed => _isVisionStreamActive && _visionSessionApproved && _visionUserApproved && BinaryVision;

        internal void StartCapture(int fps)
        {
            if (!BinaryVision)
            {
                _toTray($"START_CAPTURE:{fps}");
                return;
            }
            _visionRelay.Reset();
            _toTray($"START_CAPTURE_V2:{fps}");
            if (_visionUserApproved) _toTray("CLIPBOARD_SHARE:1");
        }

        // Görüntüleyiciden (oturumu tutan yönetici): select_monitor, set_quality, clipboard. Onaylı oturum gerekir.
        internal async Task HandleVisionControlAsync(JsonElement root)
        {
            if (root.TryGetProperty("device", out JsonElement device) && device.ValueKind == JsonValueKind.String && device.GetString() != _hwId()) return;
            string action = (root.TryGetProperty("action", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null) ?? "vision_control";
            var (denial, denialReason) = VisionDenial(isInputEvent: true);
            if (denial != null)
            {
                await _gate.DenyAsync(denial, action, reason: denialReason);
                return;
            }
            string trayMessage = VisionRelay.TrayMessageFor(root, ClipboardAllowed, out LocalAuditEvent audit);
            if (trayMessage == null)
            {
                POpsHelpers.Log("VISION", $"Görüntüleyici mesajı uygulanmadı ({LogText.Safe(action, 40)}): geçersiz, çok büyük ya da pano için kabul edilmiş oturum yok.");
                return;
            }
            if (audit != null) _audit(audit);
            _toTray(trayMessage);
        }

        private async Task<bool> SendVisionBinaryAsync(ReadOnlyMemory<byte> frame)
        {
            var ws = _visionWs;
            if (ws == null || ws.State != WebSocketState.Open) return false;
            if (!await _wsVisionLock.WaitAsync(1500)) return false;
            try
            {
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
                return true;
            }
            catch (Exception) { return false; }
            finally { _wsVisionLock.Release(); }
        }

        private async Task<bool> SendVisionTextAsync(object payload)
        {
            var ws = _visionWs;
            if (ws == null || ws.State != WebSocketState.Open) return false;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            if (!await _wsVisionLock.WaitAsync(1500)) return false;
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            }
            catch (Exception) { return false; }
            finally { _wsVisionLock.Release(); }
        }

        private void ApplyVisionClose(WebSocketCloseStatus? status)
        {
            VisionCloseDecision decision = VisionChannel.OnClosed(status == null ? null : (int)status.Value);
            if (decision.AuthenticationRejected)
            {
                LocalAudit.Write(LocalAudit.AuthenticationRejected("vision"));
                POpsHelpers.Log("AGENT", "[GÜVENLİK] Sunucu Vision kanalında cihaz kimliğini reddetti (4401); tünel yeniden denenmeyecek.", true);
            }
            if (decision.ClearStream) _isVisionStreamActive = false;
            if (decision.ClearApproval) _visionSessionApproved = false;
        }

        // ------------------------------------------------------------------ sunucu komutları (bkz. VisionHandler)
        // start_stream (eski): kullanıcı onayı olmadan, zorunlu oturum gibi
        internal async Task StartStreamAsync(JsonElement root, CancellationToken stoppingToken)
        {
            if (!_visionAuditActive)
            {
                _visionSessionId = null;
                _visionRequestedBy = null;
                _pendingVisionMandatory = true;
            }
            await ConnectVisionTunnelAsync(stoppingToken);
            int fps = root.TryGetProperty("fps", out var fProp) ? (fProp.ValueKind == JsonValueKind.Number ? fProp.GetInt32() : 2) : 2;
            if (_isVisionStreamActive)
            {
                if (!_visionAuditActive)
                {
                    _visionUserApproved = false;
                    _audit(LocalAudit.VisionStarted(_visionSessionId, _visionRequestedBy, false));
                    _visionAuditActive = true;
                }
                StartCapture(fps);
            }
        }

        // start_vision_session: oturumun bilgileri saklanır, istek olduğu gibi tepsiye gider
        internal void RequestSession(string message, JsonElement root)
        {
            _visionSessionId = root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
            _visionRequestedBy = root.TryGetProperty("requested_by", out var requester) && requester.ValueKind == JsonValueKind.String
                ? requester.GetString()
                : root.TryGetProperty("admin_name", out var admin) && admin.ValueKind == JsonValueKind.String ? admin.GetString() : null;
            _pendingVisionMandatory = root.TryGetProperty("is_mandatory", out var mandatory) && mandatory.ValueKind == JsonValueKind.True;
            _visionReason = root.TryGetProperty("reason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String ? reasonProp.GetString() : null;
            // Onay ya da geri sayım tepsidedir; oturum yalnızca tepsinin START_VISION_TUNNEL'ı ile başlar
            _toTray(message);
        }

        // Tepsiden JPEG kare (bekleyen ekran önizlemesinin değilse; bkz. RemoteInputHandler): eski (JSON) yayının karesi
        internal async Task ForwardStreamFrameAsync(byte[] jpegBytes)
        {
            if (!_isVisionStreamActive) return;

            var localWs = _visionWs;
            if (localWs == null || localWs.State != WebSocketState.Open) return;

            try
            {
                string base64Image = Convert.ToBase64String(jpegBytes);
                var framePayload = new { type = "stream_frame", hw_id = _hwId(), hostname = _pcName, image = base64Image };
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(framePayload));

                if (await _wsVisionLock.WaitAsync(1500))
                {
                    try { await localWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                    finally { _wsVisionLock.Release(); }
                }
            }
            catch { }
        }
    }
}
