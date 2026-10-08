using System;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Sunucuya komut bağlantısı (/ws/agent): kimlik başlıkları, bağlanma, alma döngüsü, heartbeat turu ve kopunca üstel geri
    // çekilme (4401 kimlik reddi, 4409 kopya). Şifresiz ve yerel olmayan sunucuya bağlanılmaz. Sunucu mesajları
    // CommandDispatcher'a gider, giden mesajlar CommandChannel'dan.
    [SupportedOSPlatform("windows")]
    internal sealed class CommandConnection
    {
        // Komut mesajının boyu sınırı (parçalı mesajlar EndOfMessage'a kadar birleştirilir; bkz. WebSocketMessages)
        private const int MaxCommandMessageBytes = 8 * 1024 * 1024;
        // X-Agent-Version (Worker.AppVersion ile aynı değer)
        private static readonly string AppVersion = POpsHelpers.AppVersion;

        private readonly string _serverUrl;
        private readonly string _pcName;
        private readonly Func<string?> _hwId;
        private readonly CommandChannel _channel;
        private readonly CommandDispatcher _dispatcher;
        private readonly UpdateReporter _updates;
        private readonly ResultOutbox _outbox;
        private readonly VisionSession _vision;
        private readonly TrayMessageRouter _tray;
        private readonly Func<QuarantineControl> _quarantine;
        private readonly Func<object?> _dna;
        private readonly AgentHealthTelemetry _health;
        private readonly AgentStartupHealth _startupHealth;
        private readonly Func<Task> _slowInitialization;
        private readonly Action _socketOpened;
        private readonly Action _channelConnected;
        private readonly Action<LocalAuditEvent> _audit;

        private bool _commandUsesDeviceSecret;
        private bool _cloneRejectedAudited;

        // Kimlik, karantina denetimi ve donanım bilgisi çalışırken değişir (set_identity, testler, yavaş açılış): her
        // kullanımda okunur. slowInitialization: bağlantıdan sonra, alma döngüsünden önce beklenen açılış işi;
        // socketOpened: yeni bağlantıda bağlantı başına durumların sıfırlanması; channelConnected: DNS politika izleme
        // (bkz. PolicySync); audit: Olay Günlüğü
        public CommandConnection(string serverUrl, string pcName, Func<string?> hwId, CommandChannel channel, CommandDispatcher dispatcher,
            UpdateReporter updates, ResultOutbox outbox, VisionSession vision, TrayMessageRouter tray, Func<QuarantineControl> quarantine,
            Func<object?> dna, AgentHealthTelemetry health, AgentStartupHealth startupHealth, Func<Task> slowInitialization,
            Action socketOpened, Action channelConnected, Action<LocalAuditEvent> audit)
        {
            _serverUrl = serverUrl;
            _pcName = pcName;
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _updates = updates ?? throw new ArgumentNullException(nameof(updates));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _vision = vision ?? throw new ArgumentNullException(nameof(vision));
            _tray = tray ?? throw new ArgumentNullException(nameof(tray));
            _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
            _dna = dna ?? throw new ArgumentNullException(nameof(dna));
            _health = health ?? throw new ArgumentNullException(nameof(health));
            _startupHealth = startupHealth ?? throw new ArgumentNullException(nameof(startupHealth));
            _slowInitialization = slowInitialization ?? throw new ArgumentNullException(nameof(slowInitialization));
            _socketOpened = socketOpened ?? throw new ArgumentNullException(nameof(socketOpened));
            _channelConnected = channelConnected ?? throw new ArgumentNullException(nameof(channelConnected));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }

        // Komut bağlantısı cihaz secret'ıyla mı kuruldu (set_bypass_secret yalnızca öyleyse kabul edilir; bkz. SecretsHandler)
        internal bool UsesDeviceSecret => _commandUsesDeviceSecret;

        // Bağlanır, heartbeat turunu sürdürür, kopunca geri çekilip yeniden bağlanır (servis durana kadar)
        internal async Task RunAsync(CancellationToken stoppingToken)
        {
            string baseWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");

            // Son sağlam bağlantıdan beri art arda başarısız bağlantı sayısı (bkz. ReconnectBackoff)
            int reconnectAttempt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                string commandWsUrl = $"{baseWsUrl}/ws/agent/{_hwId()}";

                // Tepsi borusu bir kez açılır ve sunucu bağlantısından bağımsız açık kalır (bkz. OnCommandConnectionLostAsync)
                _tray.EnsureTrayPipeServer();
                ClientWebSocket commandWs = new ClientWebSocket();
                _channel.Socket = commandWs;
                commandWs.Options.RemoteCertificateValidationCallback = ServerTrust.WebSocketCallback(new Uri(commandWsUrl));
                AgentHeaders(commandWs);
                string authMode = ApplyAuthHeaders(commandWs);
                POpsHelpers.Log("AGENT", $"[POps V4] DUAL-SOCKET MİMARİSİ BAŞLATILDI ({POpsHelpers.AppVersion}, kimlik: {authMode})");
                _startupHealth.Mark(StartupCheck.Loop);

                try
                {
                    await commandWs.ConnectAsync(new Uri(commandWsUrl), stoppingToken);
                    POpsHelpers.Log("AGENT", "[+] Ana Komut Tüneli Kuruldu.");
                    // Sunucunun sonuçları onaylayıp onaylamadığı her bağlantıda yeniden öğrenilir (server_info)
                    _socketOpened();
                    _channelConnected();

                    await _slowInitialization();

                    _ = ReceiveCommandsAsync(commandWs, stoppingToken);

                    bool capabilitiesReported = false;
                    while (commandWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        // İlk mesaj daima dna_payload'lı heartbeat'tir (sunucu kimliği ondan çözer)
                        await SendHeartbeatAsync(stoppingToken);
                        if (!capabilitiesReported)
                        {
                            await _channel.TrySendAsync(AgentCapabilities.StatusMessage());
                            capabilitiesReported = true;
                        }
                        await _updates.ForwardUpdateProgressAsync();
                        await _updates.ReportUpdateResultAsync(stoppingToken);
                        await _outbox.FlushPendingResultsAsync();
                        await Task.Delay(5000, stoppingToken);
                        // İlk mesajlar gitti ve sunucu bağlantıyı bir heartbeat aralığı boyunca açık tuttu (kimliği
                        // reddetseydi ilk mesajı okuyunca 4401 ile kapatırdı): bağlantı sağlam, geri çekilme sıfırlanır
                        if (commandWs.State == WebSocketState.Open) reconnectAttempt = 0;
                    }
                }
                catch (Exception ex)
                {
                    _health.RecordError("connection", ex.Message);
                    POpsHelpers.Log("AGENT", $"[!] Santralle bağlantı koptu: {ex.Message}", true);
                }

                // Sabit aralık yerine üstel geri çekilme + full jitter: sunucu yeniden başlayınca ajanlar aynı anda gelmez.
                // Reddedilen her bağlantı sunucuda denetim kaydı açar; kimlik reddinde en az 60 sn, kopya reddinde
                // (4409: bu kimlik başka bir bilgisayarda bağlı) en az 10 dk beklenir.
                var rejection = ReconnectBackoff.FromCloseStatus((int?)commandWs.CloseStatus);
                bool authRejected = rejection == ReconnectBackoff.Rejection.Auth;
                TimeSpan wait = ReconnectBackoff.Delay(reconnectAttempt, rejection, Random.Shared);
                reconnectAttempt = ReconnectBackoff.NextAttempt(reconnectAttempt);
                // Karantinada art arda 3 bağlantı hatası: sunucunun adresi değişmiş olabilir, izin listesi beklemeden yenilenir
                if (reconnectAttempt == NetworkIsolation.RefreshAfterFailures && NetworkIsolation.IsActive)
                    _ = Task.Run(() => NetworkIsolation.RefreshServerAddressesAsync(_serverUrl, "art arda 3 bağlantı hatası"));
                if (authRejected)
                {
                    _audit(LocalAudit.AuthenticationRejected("command"));
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] Sunucu ajan kimliğini reddetti (4401): geçerli bir enroll jetonu gerekiyor. {wait.TotalSeconds:0} sn sonra yeniden denenecek.", true);
                }
                else if (rejection == ReconnectBackoff.Rejection.Clone)
                    OnCloneRejected(wait);
                else
                    POpsHelpers.Log("AGENT", $"Sunucuya {wait.TotalSeconds:0.0} sn sonra yeniden bağlanılacak.");

                await OnCommandConnectionLostAsync();
                await Task.Delay(wait, stoppingToken);
            }

            // Her yeni sokete ajanın sürümü, hemen ardından özellikleri (sertifika denetiminden sonra, kimlik başlıklarından
            // önce). PeerCacheTests ve PowerMessageTests bu iki satırı bu dosyanın metninde arar.
            static void AgentHeaders(ClientWebSocket _commandWs)
            {
                _commandWs.Options.SetRequestHeader("X-Agent-Version", AppVersion);
                _commandWs.Options.SetRequestHeader(AgentFeatures.HeaderName, AgentFeatures.Header);
            }
        }

        // Şifresiz (http/ws) ve yerel olmayan sunucuya bağlanılmaz (bkz. POpsHelpers.IsSecureServerUrl).
        // Tepsi ve watchdog yerel işler (ör. karantinada çevrimdışı bypass) için yine çalışır.
        internal async Task RunWithoutServerAsync(CancellationToken stoppingToken)
        {
            _tray.EnsureTrayPipeServer();
            _startupHealth.Mark(StartupCheck.Loop);
            while (!stoppingToken.IsCancellationRequested)
            {
                POpsHelpers.Log("AGENT", $"[GÜVENLİK] ServerUrl şifresiz http ve yerel değil ({_serverUrl}); cihaz secret'ı ve komutlar ağda açık gideceği için sunucuya bağlanılmıyor. https:// bir adres verin (MSI: SERVER_URL=https://...).", true);
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }

        // Cihaz secret'ı varsa X-Agent-Secret, enroll jetonu varsa X-Enroll-Token gönderilir. Sunucu önce
        // secret'a bakar; secret geçersizse (ör. dondurma ile kaybolmuşsa) jetonla yeniden kayıt olunabilir.
        // Değerler loglanmaz; dönen metin yalnızca hangi başlıkların gittiğini söyler.
        private string ApplyAuthHeaders(ClientWebSocket ws)
        {
            _commandUsesDeviceSecret = false;
            if (!POpsHelpers.IsSecureServerUrl(_serverUrl)) return "yok (şifresiz bağlantı)";

            string secret = AgentCredentials.CurrentSecret ?? AgentCredentials.LoadSecret();
            string enrollToken = AgentCredentials.GetEnrollToken();
            _commandUsesDeviceSecret = secret != null;

            if (secret != null) ws.Options.SetRequestHeader("X-Agent-Secret", secret);
            if (enrollToken != null) ws.Options.SetRequestHeader("X-Enroll-Token", enrollToken);

            if (secret != null && enrollToken != null) return "secret + enroll jetonu";
            if (secret != null) return "secret";
            if (enrollToken != null) return "enroll jetonu";
            return "yok";
        }

        // 4409: sunucu bu kimliği başka bir bilgisayarda bağlı buldu (kopyalanmış kurulum, asıl cihaz bağlı).
        // Olay Günlüğüne çalışma başına bir kez yazılır; log her kopuşta.
        internal void OnCloneRejected(TimeSpan wait)
        {
            if (!_cloneRejectedAudited) _audit(LocalAudit.CloneRejected("command"));
            _cloneRejectedAudited = true;
            POpsHelpers.Log("AGENT", $"[GÜVENLİK] Sunucu bu cihaz kimliğinin ({_hwId()}) başka bir bilgisayarda bağlı olduğunu bildirdi (4409): "
                + $"bu kurulum kopyalanmış olabilir (bkz. POpsAgent.exe --generalize). {wait.TotalMinutes:0.0} dk sonra yeniden denenecek.", true);
        }

        // Sunucu bağlantısı koptu. Tepsi borusu açık kalır: sunucuya ulaşılamazken de çevrimdışı bypass kodu, kilit ekranı
        // ve yardım masası mesajları servise ulaşmalı. 0.1.22'ye kadar boru her kopuşta kapatılıp ancak bir sonraki
        // bağlanma denemesinde açılıyordu; geri çekilme beklemesinde (60 sn'ye kadar, 4401'de 2 dk, 4409'da 11 dk) tepsiye
        // yazılan kod sessizce kayboluyordu. Uzaktan izleme ve girdi biter: tepsi yakalamayı durdurur, basılı kalan uzak
        // tuşları bırakır (eskiden borunun kapanması bunu sağlıyordu).
        internal async Task OnCommandConnectionLostAsync()
        {
            bool visionActive = _vision.StreamActive || _vision.SessionApproved || _vision.HasTunnel;
            _vision.RevokeApproval();
            if (visionActive) _tray.Pipe?.SendCommandToDesktop("STOP_CAPTURE");
            await _vision.DisconnectVisionTunnelAsync();
        }

        private async Task ReceiveCommandsAsync(ClientWebSocket ws, CancellationToken stoppingToken)
        {
            var buffer = new byte[16384];
            while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var (message, messageType) = await WebSocketMessages.ReceiveTextAsync(ws, buffer, MaxCommandMessageBytes, stoppingToken);
                    if (messageType == WebSocketMessageType.Close) break;
                    await _dispatcher.DispatchAsync(message, ws, stoppingToken);
                }
                catch (WebSocketMessages.TooLargeException ex)
                {
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] Sunucu mesajı yok sayıldı: {ex.Message}.", true);
                }
                catch (JsonException ex)
                {
                    POpsHelpers.Log("AGENT", $"Sunucu mesajı çözümlenemedi, yok sayıldı: {ex.Message}", true);
                }
                catch (Exception ex) when (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    POpsHelpers.Log("AGENT", $"Sunucu mesajı işlenemedi: {ex.Message}", true);
                }
                catch { }
            }
        }

        // Ön plandaki uygulamanın adı (tepsiden; pencere başlığı gönderilmez) ve karantina durumu. Sunucu (anahtarlı
        // bağlantıda) "quarantined" ile bekleyen kilit/açma isteğini tamamlar ya da yeniden gönderir; bekleyen istek
        // yoksa panel ajanın gerçek durumunu gösterir. Donanım bilgisi okunamadıysa dna_payload boş nesnedir (null
        // değil; şema nesne ister, sunucu ikisini de boş sayar).
        internal object HeartbeatPayload() => new
        {
            hw_id = _hwId(),
            hostname = _pcName,
            lab_name = "Atanmamis_Cihazlar",
            status = "Online",
            active_window = _tray.ForegroundApp ?? "-",
            quarantined = _quarantine().IsLocked,
            dna_payload = _dna() ?? new object(),
            agent_health = _health.Snapshot(
                _tray.Pipe?.IsConnected == true,
                AgentCapabilities.VisionEnabled,
                _vision.TunnelOpen,
                _quarantine().ScreenLocked,
                _quarantine().NetworkIsolated,
                _quarantine().LastIsolationError)
        };

        // Heartbeat: o anki komut soketine, yalnızca açıksa (bkz. CommandChannel.SendHeartbeatAsync); gittiyse bu bağlantıda
        // update_progress gönderilebilir
        private async Task SendHeartbeatAsync(CancellationToken token)
        {
            string json = JsonSerializer.Serialize(HeartbeatPayload());
            var bytes = Encoding.UTF8.GetBytes(json);

            await _channel.SendHeartbeatAsync(bytes, _updates.OnHeartbeatSent, token);
        }
    }
}
