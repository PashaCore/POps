using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using POpsAgent;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CA1416
#nullable disable

namespace POpsAgent
{
    public class AgentPolicy
    {
        public string fair_use_text { get; set; } = "";
        public List<string> dns_categories { get; set; } = new List<string>();
        // Kategori -> alan adları (tam eşleşme ya da alt alan; bkz. DnsWatch). Yoksa DNS tespiti yapılmaz.
        public Dictionary<string, List<string>> dns_domains { get; set; } = new Dictionary<string, List<string>>();
        public bool auto_quarantine { get; set; } = false;
        public int quarantine_threshold { get; set; } = 3;
    }

    [SupportedOSPlatform("windows")]
    public class Worker : BackgroundService
    {
        // Sürüm kök VERSION dosyasından gelir (Directory.Build.props -> assembly). Elle güncellenmez.
        public static readonly string APP_VERSION = POpsHelpers.AppVersion;

        private readonly ILogger<Worker> _logger;
        private readonly string _pcName;
        private string _hwId;
        private readonly string _identityFilePath = @"C:\POpsData\identity.key";
        private readonly HttpClient _httpClient;
        private readonly AgentStartupHealth _startupHealth;
        private readonly AgentHealthTelemetry _health = new AgentHealthTelemetry();
        private Task _slowInitialization;

        // 🚀 ARTIK SABİT DEĞİL, HELPERS'TAN OKUNACAK
        private string _serverUrl;

        // Sunucu kimliksiz ajanı reddederken WebSocket'i bu kodla kapatır (enforce_agent_auth açıkken)
        private const WebSocketCloseStatus AuthRejectedCloseStatus = (WebSocketCloseStatus)4401;

        private ClientWebSocket _commandWs;
        private bool _commandUsesDeviceSecret;
        private ClientWebSocket _visionWs;
        private readonly SemaphoreSlim _wsCommandLock = new(1, 1);
        private readonly SemaphoreSlim _wsVisionLock = new(1, 1);

        private TrayPipeServer _trayPipe;
        private volatile bool _isVisionStreamActive;
        // Uzaktan fare/klavye yalnızca kullanıcının tepsi üzerinden onayladığı (ya da zorunlu oturumda bildirimin
        // gösterildiği) Vision oturumu açıkken uygulanır. Sunucu ele geçirilse bile yerel onay olmadan girdi yok.
        private volatile bool _visionSessionApproved;
        private string _visionSessionId;
        private string _visionRequestedBy;
        private bool _visionUserApproved;
        private bool _visionAuditActive;
        private bool _pendingVisionMandatory;
        private TaskCompletionSource<byte[]> _thumbnailTcs;


        private object _cachedDna = null;
        private object _cachedInventory = null;

        private AgentPolicy _currentPolicy = new AgentPolicy();
        private bool _fairUseAcknowledged = false;

        // Karantina (lockdown/unlock/çevrimdışı bypass) ve Windows Update: bkz. QuarantineControl, PatchManager
        private readonly QuarantineControl _quarantine;
        private readonly PatchManager _patches;
        // Yardım masası: tepsinin "Sorun bildir" / "Taleplerim" istekleri (bkz. Helpdesk)
        private readonly Helpdesk _helpdesk;
        // "Etkinlik geçmişim": yöneticilerin bu cihazda yaptığı işlemler (bkz. ActivityHistory)
        private readonly ActivityHistory _activity;
        // Ön plandaki uygulamanın süreç adı (tepsiden, yalnızca ad; bkz. ActiveApp). Bilinmiyorsa null.
        private volatile string _activeApp;

        public Worker(ILogger<Worker> logger) : this(logger, new AgentStartupHealth(false)) { }

        public Worker(ILogger<Worker> logger, AgentStartupHealth startupHealth)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("POpsV", StringComparer.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n========================================");
                Console.WriteLine($" POps Agent - Sürüm: {APP_VERSION}");
                Console.WriteLine($"========================================\n");
                Console.ResetColor();
                Environment.Exit(0);
            }

            _logger = logger;
            _startupHealth = startupHealth ?? throw new ArgumentNullException(nameof(startupHealth));
            _pcName = Environment.MachineName;
            // Politika ve /updates paket indirme: sunucu sertifikası da ServerTrust ile doğrulanır
            _httpClient = new HttpClient(ServerTrust.NewHandler());

            // 🚀 IP'Yİ CONFIG DOSYASINDAN AL
            _serverUrl = POpsHelpers.GetServerUrl();
            POpsHelpers.Log("AGENT", $"POps Agent Başlatılıyor (Hedef: {_serverUrl})");
            foreach (string configPath in POpsHelpers.ConfigPaths) SecureConfigFile(configPath);

            _quarantine = new QuarantineControl(message => _trayPipe?.SendCommandToDesktop(message),
                EnableNetworkIsolationAsync, DisableNetworkIsolationAsync, audit: LocalAudit.Write);
            DnsPolicyMonitor.ErrorReporter = message => _health.RecordError("dns", message);
            // DNS eşiğindeki otomatik karantina da kilit ekranı + yalıtım yolundan geçer (bkz. AutoQuarantineAsync)
            DnsPolicyMonitor.Quarantine = reason => _ = AutoQuarantineAsync(reason);
            _patches = new PatchManager(_serverUrl, () => _hwId);
            // Talebin sahibi konsoldaki değil, isteği yapan tepsinin oturumundaki kullanıcı (hızlı kullanıcı değiştirme, RDP)
            _helpdesk = new Helpdesk(_serverUrl, () => _hwId, () => _trayPipe?.ClientUser, message => _trayPipe?.SendCommandToDesktop(message));
            _activity = new ActivityHistory(_serverUrl, () => _hwId, message => _trayPipe?.SendCommandToDesktop(message));
        }

        // Yavaş olabilen açılış işleri (WMI donanım sorguları, kimlik, güvenli depo). ExecuteAsync bunları arka
        // planda çalıştırır: servisin açılışını ve updater'ın beklediği health.json'u bekletmezler.
        // Kimlik önce kurulur; envanter hw_id'yi ondan alır.
        private void InitializeCoreState()
        {
            _startupHealth.Run(StartupCheck.Identity, () => _hwId = InitializeIdentity());
            POpsHelpers.Log("AGENT", $"Kimlik Başlatıldı: {_hwId}");

            _startupHealth.Run(StartupCheck.Credentials, () =>
            {
                AgentCredentials.Initialize();
                AgentCredentials.LoadSecret();
            });
            _startupHealth.Run(StartupCheck.Capabilities, AgentCapabilities.Load);
            // Karantina yeniden başlatmadan sonra sürüyorsa Ctrl+Alt+Del seçenekleri yeniden kapatılır; sürmüyorsa kalıntı temizlenir
            KioskMode.Sync(_quarantine.IsLocked);
            if (_quarantine.IsLocked) LocalAudit.Write(LocalAudit.QuarantineStarted("açılış"));
            // Kurum sertifikası (server-ca.pem) varsa sunucu yalnızca onunla doğrulanır; kip loglanır
            ServerTrust.Reload();
        }

        private void InitializeSlowState()
        {
            try
            {
                _cachedDna = GetHardwareDnaInternal();
                _cachedInventory = BuildInventoryInternal();
            }
            catch (Exception ex)
            {
                // Hata burada kalır: bağlantı döngüsü bu görevi bekler, istisna her yeniden bağlanışta tekrar fırlamasın
                _health.RecordError("inventory", ex.Message);
                POpsHelpers.Log("AGENT", $"Donanım bilgisi toplanamadı: {ex.Message}", true);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Tepsi ve watchdog kullanıcı oturumunda yoksa başlatılır (kurulum/güncelleme sonrası, karantinada kilit ekranı).
            // Yavaş WMI açılışını beklemez.
            _ = Task.Run(() => new UserSessionApps().RunAsync(stoppingToken));

            await Task.Run(InitializeCoreState, stoppingToken);
            _slowInitialization = Task.Run(InitializeSlowState, stoppingToken);
            AgentUpdate.LogLastResult();

            if (!POpsHelpers.IsSecureServerUrl(_serverUrl))
            {
                StartTrayPipeServer();
                _startupHealth.Mark(StartupCheck.Loop);
                await RunWithoutServerAsync(stoppingToken);
                return;
            }

            string baseWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");

            // Start background tasks
            _ = Task.Run(() => PolicyPollingLoop(stoppingToken));

            // Sunucuya bildirimler (yalnızca cihaz secret'ı varken; bkz. AgentHttp): yazılım envanteri (açılıştan
            // kısa süre sonra, sonra 6 saatte bir), günlük Windows Update taraması, oturum açma/kapama
            var sessions = new SessionReporter(_serverUrl, () => _hwId, _pcName,
                error => _health.RecordError("session", error));
            sessions.UserChanged += _ =>
            {
                _activeApp = null;
                // Yeni kullanıcı öncekinin DNS ihlalleriyle karantinaya girmesin
                DnsPolicyMonitor.OnUserChanged();
            };
            _ = Task.Run(() => new SoftwareReporter(_serverUrl, () => _hwId,
                _health.InventoryUploaded, error => _health.RecordError("inventory", error)).RunAsync(stoppingToken));
            _ = Task.Run(() => _patches.ScheduleLoopAsync(stoppingToken));
            _ = Task.Run(() => sessions.RunAsync(stoppingToken));
            _ = Task.Run(() => _helpdesk.PollLoopAsync(stoppingToken, () => _trayPipe?.IsConnected == true));

            // Son sağlam bağlantıdan beri art arda başarısız bağlantı sayısı (bkz. ReconnectBackoff)
            int reconnectAttempt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                string commandWsUrl = $"{baseWsUrl}/ws/agent/{_hwId}";

                StartTrayPipeServer();
                _commandWs = new ClientWebSocket();
                _commandWs.Options.RemoteCertificateValidationCallback = ServerTrust.WebSocketCallback(new Uri(commandWsUrl));
                _commandWs.Options.SetRequestHeader("X-Agent-Version", APP_VERSION);
                string authMode = ApplyAuthHeaders(_commandWs);
                POpsHelpers.Log("AGENT", $"[POps V4] DUAL-SOCKET MİMARİSİ BAŞLATILDI ({APP_VERSION}, kimlik: {authMode})");
                _startupHealth.Mark(StartupCheck.Loop);

                try
                {
                    await _commandWs.ConnectAsync(new Uri(commandWsUrl), stoppingToken);
                    POpsHelpers.Log("AGENT", "[+] Ana Komut Tüneli Kuruldu.");
                    OnCommandChannelConnected();

                    await _slowInitialization;

                    _ = ReceiveCommandsAsync(_commandWs, stoppingToken);

                    bool capabilitiesReported = false;
                    while (_commandWs.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                    {
                        // İlk mesaj daima dna_payload'lı heartbeat'tir (sunucu kimliği ondan çözer)
                        await SendHeartbeatAsync(stoppingToken);
                        if (!capabilitiesReported)
                        {
                            await SendCommandMessageAsync(AgentCapabilities.StatusMessage());
                            capabilitiesReported = true;
                        }
                        await ReportUpdateResultAsync(stoppingToken);
                        await Task.Delay(5000, stoppingToken);
                        // İlk mesajlar gitti ve sunucu bağlantıyı bir heartbeat aralığı boyunca açık tuttu (kimliği
                        // reddetseydi ilk mesajı okuyunca 4401 ile kapatırdı): bağlantı sağlam, geri çekilme sıfırlanır
                        if (_commandWs.State == WebSocketState.Open) reconnectAttempt = 0;
                    }
                }
                catch (Exception ex)
                {
                    _health.RecordError("connection", ex.Message);
                    POpsHelpers.Log("AGENT", $"[!] Santralle bağlantı koptu: {ex.Message}", true);
                }

                // Sabit aralık yerine üstel geri çekilme + full jitter: sunucu yeniden başlayınca ajanlar aynı anda gelmez.
                // Reddedilen her bağlantı sunucuda denetim kaydı açar; kimlik reddinde en az 60 sn beklenir.
                bool authRejected = _commandWs.CloseStatus == AuthRejectedCloseStatus;
                TimeSpan wait = ReconnectBackoff.Delay(reconnectAttempt, authRejected, Random.Shared);
                reconnectAttempt = ReconnectBackoff.NextAttempt(reconnectAttempt);
                if (authRejected)
                {
                    LocalAudit.Write(LocalAudit.AuthenticationRejected("command"));
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] Sunucu ajan kimliğini reddetti (4401): geçerli bir enroll jetonu gerekiyor. {wait.TotalSeconds:0} sn sonra yeniden denenecek.", true);
                }
                else
                    POpsHelpers.Log("AGENT", $"Sunucuya {wait.TotalSeconds:0.0} sn sonra yeniden bağlanılacak.");

                _trayPipe?.Stop();
                await DisconnectVisionTunnelAsync();
                await Task.Delay(wait, stoppingToken);
            }
        }

        // Komut tüneli kuruldu: DNS politika izleme başlar (yalnızca ilk bağlantıda; sonra açık kalır). Politika ve
        // dns_domains her dakika PolicyPollingLoop'ta yenilenir.
        internal void OnCommandChannelConnected()
        {
            DnsPolicyMonitor.Configure(_currentPolicy, _hwId, _serverUrl);
            DnsPolicyMonitor.Start();
        }

        // Şifresiz (http/ws) ve yerel olmayan sunucuya bağlanılmaz (bkz. POpsHelpers.IsSecureServerUrl).
        // Tepsi ve watchdog yerel işler (ör. karantinada çevrimdışı bypass) için yine çalışır.
        private async Task RunWithoutServerAsync(CancellationToken stoppingToken)
        {
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

        // Enroll jetonuyla kaydolan ajana sunucu kalıcı secret'ı bir kez gönderir.
        private void HandleSetSecret(JsonElement root)
        {
            string secret = root.TryGetProperty("secret", out var sProp) && sProp.ValueKind == JsonValueKind.String ? sProp.GetString() : null;
            if (!AgentCredentials.IsWellFormed(secret))
            {
                POpsHelpers.Log("AGENT", "[GÜVENLİK] set_secret yok sayıldı: secret biçimi geçersiz.", true);
                return;
            }

            if (AgentCredentials.SaveSecret(secret, _hwId))
            {
                AgentCredentials.ForgetEnrollToken();
                POpsHelpers.Log("AGENT", "Cihaz secret'ı alındı ve güvenli depoya yazıldı; sonraki bağlantılar secret ile doğrulanacak.");
            }
            else
            {
                POpsHelpers.Log("AGENT", "Cihaz secret'ı alındı ancak diske yazılamadı; servis yeniden başlayana kadar bellekte tutuluyor.", true);
            }
        }

        private async Task PolicyPollingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string apiUrl = _serverUrl.TrimEnd('/') + "/api/agent_policies";
                    string json = await _httpClient.GetStringAsync(apiUrl, token);
                    var policy = JsonSerializer.Deserialize<AgentPolicy>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (policy != null)
                    {
                        _currentPolicy = policy;
                        DnsPolicyMonitor.Configure(policy, _hwId, _serverUrl);
                        _health.PolicySynced();
                        
                        if (!string.IsNullOrWhiteSpace(policy.fair_use_text) && !_fairUseAcknowledged)
                        {
                            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(policy.fair_use_text));
                            _trayPipe?.SendCommandToDesktop($"SHOW_FAIR_USE:{b64}");
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _health.RecordError("policy", ex.Message);
                    POpsHelpers.Log("AGENT", $"Politika eşitleme başarısız: {ex.Message}", true);
                }
                await Task.Delay(60000, token); // Poll every minute
            }
        }

        // Boru dinlemeye geçince sağlık kontrolü işaretlenir (bkz. AgentStartupHealth). Bağlantı döngüsü bunu
        // beklemez: boru adı başka bir süreçte kalırsa (ör. yerel bir kullanıcı adı önceden aldıysa) tepsi çalışmaz
        // ama ajan sunucuya yine bağlanır; health.json yazılmadığı için güncelleme de başarılı sayılmaz.
        private void StartTrayPipeServer()
        {
            _trayPipe?.Stop();
            _trayPipe = new TrayPipeServer(_logger, _hwId, _httpClient, _serverUrl);

            _trayPipe.OnMessageReceived += (message) =>
            {
                if (message.StartsWith("USER_COMMAND:"))
                {
                    // Eski tepsi sürümlerinin "WatchDog'u / ekran izlemeyi duraklat" komutları artık kabul edilmez
                    POpsHelpers.Log("AGENT", $"Yok sayılan kullanıcı komutu: {message}");
                }
                else if (message == "FAIR_USE_ACK")
                {
                    _fairUseAcknowledged = true;
                    POpsHelpers.Log("AGENT", "Kullanıcı aydınlatma metnini onayladı.");
                }
                else if (message.StartsWith("ACTIVE_APP:"))
                {
                    // Yalnızca süreç adı; heartbeat'te active_window olarak gider
                    string app = ActiveApp.Sanitize(message.Substring("ACTIVE_APP:".Length));
                    if (app != null) _activeApp = app;
                }
                else if (message.StartsWith("ACTIVE_WINDOW:"))
                {
                    // Eski tepsi pencere başlığı gönderir: KVKK gereği sunucuya iletilmez (bkz. ActiveApp)
                }
                else if (message.StartsWith("START_VISION_TUNNEL"))
                {
                    int fps = 2;
                    if (message.Contains(":")) int.TryParse(message.Split(':')[1], out fps);
                    _ = Task.Run(async () =>
                    {
                        await ConnectVisionTunnelAsync(CancellationToken.None);
                        // Tünel açılmadıysa (Vision kapalı, şifresiz sunucu, bağlantı hatası) ekran yakalanmaz.
                        // İstek doğrulanmış tepsiden, kullanıcı onayından sonra geldiği için oturum onaylıdır.
                        if (_isVisionStreamActive)
                        {
                            _visionSessionApproved = true;
                            _visionUserApproved = !_pendingVisionMandatory;
                            if (!_visionAuditActive)
                            {
                                LocalAudit.Write(LocalAudit.VisionStarted(_visionSessionId, _visionRequestedBy, _visionUserApproved));
                                _visionAuditActive = true;
                            }
                            _trayPipe?.SendCommandToDesktop($"START_CAPTURE:{fps}");
                        }
                    });
                }
                else if (message.StartsWith("REJECT_VISION_TUNNEL:"))
                {
                    _visionSessionApproved = false;
                    string sessionId = message.Split(':')[1];
                    var payload = new { type = "vision_rejected", session_id = sessionId, hw_id = _hwId };
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                    _ = Task.Run(async () =>
                    {
                        if (await _wsCommandLock.WaitAsync(2000))
                        {
                            try { if (_commandWs != null && _commandWs.State == WebSocketState.Open) await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                            finally { _wsCommandLock.Release(); }
                        }
                    });
                }
                else if (message == "STOP_VISION_TUNNEL")
                {
                    _visionSessionApproved = false;
                    _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
                    _ = DisconnectVisionTunnelAsync();
                }
                else if (message.StartsWith("UNLOCK_BYPASS:"))
                {
                    _ = HandleBypassAttemptAsync(message.Substring("UNLOCK_BYPASS:".Length));
                }
                else if (message.StartsWith("TICKET_CREATE:"))
                {
                    _ = _helpdesk.CreateAsync(message.Substring("TICKET_CREATE:".Length));
                }
                else if (message == "TICKET_LIST")
                {
                    _ = _helpdesk.ListAsync();
                }
                else if (message == "ACTIVITY_LIST")
                {
                    _ = _activity.ListAsync();
                }
            };

            _trayPipe.OnFrameReceived += async (jpegBytes) =>
            {
                var tcs = Interlocked.Exchange(ref _thumbnailTcs, null);
                if (tcs != null)
                {
                    tcs.TrySetResult(jpegBytes);
                    return;
                }
                
                if (!_isVisionStreamActive) return;
                
                var localWs = _visionWs;
                if (localWs == null || localWs.State != WebSocketState.Open) return;
                
                try
                {
                    string base64Image = Convert.ToBase64String(jpegBytes);
                    var framePayload = new { type = "stream_frame", hw_id = _hwId, hostname = _pcName, image = base64Image };
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(framePayload));
                    
                    if (await _wsVisionLock.WaitAsync(1500))
                    {
                        try { await localWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                        finally { _wsVisionLock.Release(); }
                    }
                }
                catch { }
            };

            _trayPipe.OnDisconnected += () =>
            {
                _visionSessionApproved = false;
                _activeApp = null;
            };

            // Kilit ekranı tepsiyle birlikte kapanmış olabilir: karantina sürüyorsa yeniden gösterilir
            _trayPipe.OnConnected += () => _quarantine.SyncTray();

            _trayPipe.Start().ContinueWith(_ => _startupHealth.Mark(StartupCheck.Pipe), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        private async Task HandleSetBypassSecretAsync(JsonElement root)
        {
            string secret = root.TryGetProperty("secret", out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() : null;
            bool saved = BypassSecretCommand.Process(secret, _commandUsesDeviceSecret,
                AgentCredentials.SaveDeviceBypassSecret,
                message => POpsHelpers.Log("AGENT", message,
                    message.StartsWith("[GÜVENLİK]", StringComparison.Ordinal) || message.EndsWith("yazılamadı.", StringComparison.Ordinal)),
                out string fingerprint);
            if (saved)
            {
                LocalAudit.Write(LocalAudit.BypassSecretReceived(fingerprint));
                await SendCommandMessageAsync(new { type = "bypass_secret_ack", fingerprint });
            }
        }

        // Çevrimdışı bypass kodu: geçerliyse sunucunun unlock'u ile aynı yol (kilit ekranı kapanır, yalıtım kalkar).
        // Kilit gerçekten kalktıysa ve sunucuya ulaşılabiliyorsa agent.offline_bypass olarak bildirilir (sunucu panelde
        // karantina durumunu günceller).
        private async Task HandleBypassAttemptAsync(string token)
        {
            try
            {
                string deviceSecret = AgentCredentials.GetDeviceBypassSecret(out bool deviceSecretPresent);
                if (!await _quarantine.HandleBypassAsync(token, _hwId, AgentCredentials.GetBypassSecret(),
                    deviceSecret, deviceSecretPresent, DateTime.Now)) return;
                string hwId = _hwId;
                await AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/logs/", hwId), hwId, QuarantineControl.OfflineBypassLog(), "Bypass denetim kaydı");
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Offline Bypass işlenemedi: {ex.Message}", true); }
        }

        // DNS kural ihlali eşiği aşıldı: sunucunun lockdown'u ile aynı yol (kilit ekranı + ağ yalıtımı). Sunucuya
        // ulaşılabiliyorsa agent.auto_quarantine olarak bildirilir (yalıtım sunucuya erişimi kesmez); sunucu panelde
        // karantina durumunu günceller ve bildirim gönderir.
        private async Task AutoQuarantineAsync(string reason)
        {
            try
            {
                await _quarantine.LockdownAsync(QuarantineControl.AutoQuarantineReason, "dns");
                string hwId = _hwId;
                await AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/logs/", hwId), hwId, QuarantineControl.AutoQuarantineLog(reason), "Otomatik karantina bildirimi");
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Otomatik karantina uygulanamadı: {ex.Message}", true); }
        }

        private async Task ConnectVisionTunnelAsync(CancellationToken token)
        {
            if (_visionWs != null && _visionWs.State == WebSocketState.Open) return;
            if (!AgentCapabilities.VisionEnabled)
            {
                await DenyCapabilityAsync("vision", "vision_tunnel");
                return;
            }
            // Ekran akışı ve uzaktan girdi yalnızca şifreli kanaldan (bkz. POpsHelpers.IsSecureServerUrl). Tepsi
            // START_VISION_TUNNEL'ı komut tüneli bağlı olmasa da isteyebildiği için burada ayrıca denetlenir.
            if (!POpsHelpers.IsSecureServerUrl(_serverUrl))
            {
                POpsHelpers.Log("AGENT", "[GÜVENLİK] Vision tüneli açılmadı: ServerUrl şifresiz ve yerel değil.", true);
                return;
            }
            string visionWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://") + $"/ws/vision/{_hwId}";
            string secret = AgentCredentials.CurrentSecret ?? AgentCredentials.LoadSecret();
            VisionAuthSelection auth = VisionChannel.SelectHeaders(secret, AgentCredentials.GetEnrollToken(), APP_VERSION);
            if (!auth.CanConnect)
            {
                POpsHelpers.Log("AGENT", "[GÜVENLİK] Vision tüneli açılmadı: cihaz henüz kayıtlı değil (anahtar yok)", true);
                await DenyCapabilityAsync("vision", "vision_tunnel", reason: "not_enrolled");
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
                _ = ReceiveVisionInputsAsync(_visionWs, token);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"[!] Vision Tüneli açılamadı: {ex.Message}", true);
                ApplyVisionClose(newWs.CloseStatus);
                newWs.Dispose();
            }
        }

        private async Task DisconnectVisionTunnelAsync()
        {
            _isVisionStreamActive = false;
            _visionSessionApproved = false;
            var ws = Interlocked.Exchange(ref _visionWs, null);
            if (ws != null) { try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Yayın Kesildi", CancellationToken.None); } catch { } ws.Dispose(); }
            if (_visionAuditActive)
            {
                LocalAudit.Write(LocalAudit.VisionFinished(_visionSessionId, _visionRequestedBy, _visionUserApproved));
                _visionAuditActive = false;
            }
        }

        // Mesaj boyu sınırları (parçalı mesajlar EndOfMessage'a kadar birleştirilir; bkz. WebSocketMessages)
        private const int MaxCommandMessageBytes = 8 * 1024 * 1024;
        private const int MaxVisionMessageBytes = 1024 * 1024;

        // Uzaktan fare/klavye olayı (input_type taşıyan remote_input). Ekran önizlemesi ve FPS ayarı girdi değildir.
        private static bool IsInputEvent(JsonElement root) => root.TryGetProperty("input_type", out _);

        private string VisionDenial(bool isInputEvent) => VisionInputGate.DenialReason(
            AgentCapabilities.VisionEnabled, _visionSessionApproved, _isVisionStreamActive, isInputEvent);

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
                        string targetDevice = root.GetProperty("device").GetString();
                        if (targetDevice != _hwId) continue;
                        string denial = VisionDenial(IsInputEvent(root));
                        if (denial != null) { await DenyCapabilityAsync(denial, "remote_input"); continue; }
                        _trayPipe?.SendCommandToDesktop(message);
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

        private async Task ReceiveCommandsAsync(ClientWebSocket ws, CancellationToken stoppingToken)
        {
            var buffer = new byte[16384];
            while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var (message, messageType) = await WebSocketMessages.ReceiveTextAsync(ws, buffer, MaxCommandMessageBytes, stoppingToken);
                    if (messageType == WebSocketMessageType.Close) break;
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "remote_input")
                    {
                        string targetDevice = root.TryGetProperty("device", out var devProp) ? devProp.GetString() : "";
                        if (targetDevice != _hwId) continue;

                        string act = root.TryGetProperty("action", out var actProp) ? actProp.GetString() : "";
                        // Ekran önizlemesi ve uzaktan fare/klavye Vision yeteneğidir
                        string denial = VisionDenial(IsInputEvent(root));
                        if (denial != null)
                        {
                            await DenyCapabilityAsync(denial, string.IsNullOrEmpty(act) ? "remote_input" : act);
                            continue;
                        }
                        if (act == "get_thumbnail")
                        {
                            _ = Task.Run(async () =>
                            {
                                byte[] img = await CaptureSnapshotAsync(TimeSpan.FromSeconds(5));
                                if (img != null && img.Length > 0)
                                {
                                    var payload = new { type = "thumbnail", hw_id = _hwId, image = Convert.ToBase64String(img) };
                                    byte[] b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                                    await _wsCommandLock.WaitAsync();
                                    try { if (ws.State == WebSocketState.Open) await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None); }
                                    finally { _wsCommandLock.Release(); }
                                }
                            });
                        }
                        else
                        {
                            _trayPipe?.SendCommandToDesktop(message);
                        }
                    }
                    else
                    {
                        string action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : "";
                        CommandPermission commandPermission = action == "execute"
                            ? CommandExecutionPolicy.Permission(AgentCapabilities.TerminalEnabled) : null;
                        if (action == "execute" && !commandPermission.Allowed)
                        {
                            // Görev "Running"de asılı kalmasın diye sonuç olarak da bildirilir
                            int tid = root.GetProperty("task_id").GetInt32();
                            await SendCommandMessageAsync(new { type = "result", pc_name = _hwId, task_id = tid, output = commandPermission.Rejection });
                            await DenyCapabilityAsync("terminal", "execute", tid);
                        }
                        else if (action == "execute")
                        {
                            string cmd = root.GetProperty("script_path").GetString();
                            int tid = root.GetProperty("task_id").GetInt32();
                            string requestedBy = root.TryGetProperty("requested_by", out var requestedByProperty) && requestedByProperty.ValueKind == JsonValueKind.String
                                ? requestedByProperty.GetString() : null;
                            POpsHelpers.Log("AGENT", $"Uzaktan komut çalıştırılıyor (TaskID: {tid})");
                            LocalAudit.Write(LocalAudit.CommandStarted(tid, cmd, requestedBy));
                            _ = Task.Run(async () =>
                            {
                                CommandExecutionResult execution = await ExecuteCommandAsync(cmd);
                                LocalAudit.Write(LocalAudit.CommandFinished(tid, execution.ExitCode, execution.Duration));
                                var res = new { type = "result", pc_name = _hwId, output = execution.Output, task_id = tid };
                                byte[] b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(res));
                                await _wsCommandLock.WaitAsync();
                                try { await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None); }
                                finally { _wsCommandLock.Release(); }
                            });
                        }
                        else if (action == "get_hardware") await SendHardwareInfoAsync();
                        else if (action == "set_capabilities") await HandleSetCapabilitiesAsync(root);
                        else if ((action == "start_stream" || action == "start_vision_session") && !AgentCapabilities.VisionEnabled)
                        {
                            await DenyCapabilityAsync("vision", action);
                        }
                        else if (action == "start_stream") {
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
                                    LocalAudit.Write(LocalAudit.VisionStarted(_visionSessionId, _visionRequestedBy, false));
                                    _visionAuditActive = true;
                                }
                                _trayPipe?.SendCommandToDesktop($"START_CAPTURE:{fps}");
                            }
                        }
                        else if (action == "stop_stream") { 
                            _trayPipe?.SendCommandToDesktop("STOP_CAPTURE"); 
                            await DisconnectVisionTunnelAsync(); 
                        }
                        else if (action == "update_agent")
                        {
                            JsonElement command = root.Clone();
                            _ = Task.Run(() => AgentUpdate.HandleUpdateCommandAsync(command, _httpClient, _serverUrl));
                        }
                        else if (action == "wake_peer") { WakeOnLan.Send(root.GetProperty("mac").GetString()); }
                        else if (action == "set_identity") { UpdateIdentityFile(root.GetProperty("new_hw_id").GetString()); }
                        else if (action == "set_secret") { HandleSetSecret(root); }
                        else if (action == "set_bypass_secret") { await HandleSetBypassSecretAsync(root); }
                        else if (action == "lockdown")
                        {
                            string reason = root.TryGetProperty("reason", out var rProp) && rProp.ValueKind == JsonValueKind.String ? rProp.GetString() : null;
                            await _quarantine.LockdownAsync(reason);
                        }
                        else if (action == "unlock")
                        {
                            // Panel cihazı açık gösterir; yalıtım kaldırılamadıysa denetim kaydı bunu söyler
                            if (!await _quarantine.UnlockAsync("server"))
                                await AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/logs/", _hwId), _hwId, QuarantineControl.UnlockFailedLog(), "Karantina kaldırma hatası");
                        }
                        // Windows Update: arka planda yürür, bu döngüyü bekletmez (bkz. PatchManager)
                        else if (action == "scan_updates") _patches.RequestScan();
                        else if (action == "install_updates")
                        {
                            string scope = root.TryGetProperty("scope", out var scProp) && scProp.ValueKind == JsonValueKind.String ? scProp.GetString() : null;
                            _patches.RequestInstall(scope);
                        }
                        else if (action == "start_vision_session")
                        {
                            _visionSessionId = root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
                            _visionRequestedBy = root.TryGetProperty("requested_by", out var requester) && requester.ValueKind == JsonValueKind.String
                                ? requester.GetString()
                                : root.TryGetProperty("admin_name", out var admin) && admin.ValueKind == JsonValueKind.String ? admin.GetString() : null;
                            _pendingVisionMandatory = root.TryGetProperty("is_mandatory", out var mandatory) && mandatory.ValueKind == JsonValueKind.True;
                            _trayPipe?.SendCommandToDesktop(message);
                        }
                    }
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

        private async Task<byte[]> CaptureSnapshotAsync(TimeSpan timeout)
        {
            if (_trayPipe == null) return null;
            var tcs = new TaskCompletionSource<byte[]>();
            var old = Interlocked.Exchange(ref _thumbnailTcs, tcs);
            old?.TrySetCanceled();
            try
            {
                _trayPipe.SendCommandToDesktop("CAPTURE_SNAPSHOT");
                using var cts = new CancellationTokenSource(timeout);
                cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
                return await tcs.Task;
            }
            catch { return null; }
            finally { Interlocked.CompareExchange(ref _thumbnailTcs, null, tcs); }
        }

        // Ön plandaki uygulamanın adı (tepsiden; pencere başlığı gönderilmez) ve karantina durumu. Sunucu (anahtarlı
        // bağlantıda) "quarantined" ile bekleyen kilit/açma isteğini tamamlar ya da yeniden gönderir; bekleyen istek
        // yoksa panel ajanın gerçek durumunu gösterir.
        internal object HeartbeatPayload() => new
        {
            hw_id = _hwId,
            hostname = _pcName,
            lab_name = "Atanmamis_Cihazlar",
            status = "Online",
            active_window = _activeApp ?? "-",
            quarantined = _quarantine.IsLocked,
            dna_payload = _cachedDna,
            agent_health = _health.Snapshot(
                _trayPipe?.IsConnected == true,
                AgentCapabilities.VisionEnabled,
                _visionWs?.State == WebSocketState.Open)
        };

        private async Task SendHeartbeatAsync(CancellationToken token)
        {
            string json = JsonSerializer.Serialize(HeartbeatPayload());
            var bytes = Encoding.UTF8.GetBytes(json);

            await _wsCommandLock.WaitAsync(token);
            try { if (_commandWs != null && _commandWs.State == WebSocketState.Open) await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token); }
            finally { _wsCommandLock.Release(); }
        }

        // Sunucunun "set_capabilities" isteği: yalnızca kapatma uygulanır (bkz. AgentCapabilities). Vision kapandıysa
        // süren yayın hemen durdurulur. Son durum sunucuya "capabilities" olarak bildirilir.
        private async Task HandleSetCapabilitiesAsync(JsonElement request)
        {
            var changed = AgentCapabilities.ApplyServerRequest(request);
            foreach (string capability in changed.Disabled)
                LocalAudit.Write(LocalAudit.CapabilityChanged(capability == AgentCapabilities.Terminal ? "terminal" : "vision", true, false));
            if (!AgentCapabilities.VisionEnabled && _isVisionStreamActive)
            {
                _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
                await DisconnectVisionTunnelAsync();
            }
            await SendCommandMessageAsync(AgentCapabilities.StatusMessage());
        }

        // Kapalı bir yeteneğe gelen istek loglanır ve sunucuya "capability_denied" olarak bildirilir. Uzaktan fare
        // hareketi gibi sık gelen istekler için aynı yetenek/eylem en çok dakikada bir bildirilir.
        private readonly Dictionary<string, DateTime> _lastDenialNotice = new Dictionary<string, DateTime>();

        private async Task DenyCapabilityAsync(string capability, string action, int? taskId = null, string reason = null)
        {
            string key = $"{capability}/{action}/{reason}";
            lock (_lastDenialNotice)
            {
                if (taskId == null && _lastDenialNotice.TryGetValue(key, out DateTime last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
                _lastDenialNotice[key] = DateTime.UtcNow;
            }
            if (reason == null)
                POpsHelpers.Log("POLICY", $"[GÜVENLİK] {action} reddedildi: {capability} bu cihazda kapalı (yetenek politikası).", true);
            var notice = new Dictionary<string, object> { ["type"] = "capability_denied", ["capability"] = capability, ["action"] = action };
            if (taskId != null) notice["task_id"] = taskId.Value;
            if (reason != null) notice["reason"] = reason;
            await SendCommandMessageAsync(notice);
        }

        private async Task SendCommandMessageAsync(object payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await _wsCommandLock.WaitAsync();
            try
            {
                if (_commandWs != null && _commandWs.State == WebSocketState.Open)
                    await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Sunucuya mesaj gönderilemedi: {ex.Message}", true); }
            finally { _wsCommandLock.Release(); }
        }

        // POpsUpdater'ın bıraktığı sonuç (update-result.json) sunucuya bir kez "update_result" olarak iletilir.
        // Updater sonucu yeni sürüm açıldıktan sonra yazdığı için her heartbeat'te bakılır.
        private async Task ReportUpdateResultAsync(CancellationToken token)
        {
            Dictionary<string, object> message = AgentUpdate.PendingResultMessage();
            if (message == null) return;
            // Yerel denetim izi (1030) sunucu bağlantısından bağımsız, sonuç ilk görüldüğünde
            LocalAudit.Write(AgentUpdate.PendingResultAudit(message));

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            await _wsCommandLock.WaitAsync(token);
            try
            {
                if (_commandWs == null || _commandWs.State != WebSocketState.Open) return;
                await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            finally { _wsCommandLock.Release(); }

            AgentUpdate.MarkResultReported();
            POpsHelpers.Log("UPDATE", $"Güncelleme sonucu sunucuya iletildi: {message["status"]}.");
        }

        private string InitializeIdentity()
        {
            try
            {
                string dir = Path.GetDirectoryName(_identityFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // Eski sürümler bu klasörü Everyone:FullControl ile açıyordu; oturum açan herkes kimlik
                // dosyasını değiştirip başka bir cihaz gibi bağlanabiliyordu. ACL her başlangıçta yeniden kurulur.
                SecureDataDirectory(dir);
                // Watchdog'u duraklatma özelliği kaldırıldı; eski sürümden kalan bayrak temizlenir
                try { File.Delete(Path.Combine(dir, "watchdog_pause.flag")); } catch { }

                if (File.Exists(_identityFilePath))
                {
                    string savedId = File.ReadAllText(_identityFilePath).Trim();
                    if (!string.IsNullOrEmpty(savedId) && savedId.StartsWith("HW-")) return savedId;
                }

                string newId = GenerateFallbackHash();
                File.WriteAllText(_identityFilePath, newId);
                return newId;
            }
            catch
            {
                return GenerateFallbackHash();
            }
        }

        // Yalnızca SYSTEM ve Administrators yazabilir; kullanıcı oturumunda çalışan watchdog kimliği okuyabilir.
        private static void SecureDataDirectory(string dir)
        {
            try
            {
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(dir).SetAccessControl(sec);
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"POpsData izinleri ayarlanamadı: {ex.Message}", true); }
        }

        // appsettings.json yalnızca SYSTEM ve Administrators'a açıktır; izin üst klasörden devralınmaz
        // (Program Files, Users'a okuma verir). Kurulum ya da onarım dosyayı varsayılan izinlerle yeniden
        // oluşturabildiği için ACL her açılışta kurulur ve sonuç geri okunarak doğrulanır. Gizli değerler
        // zaten bu dosyada tutulmaz (bkz. AgentCredentials.MigrateSecrets).
        private static void SecureConfigFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var file = new FileInfo(path);
                file.SetAccessControl(SecureStore.ProtectedFileSecurity());
                if (!SecureStore.IsLockedDown(file.GetAccessControl()))
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] {path} kilitlenemedi: SYSTEM/Administrators dışında erişim izni hâlâ var.", true);
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"{path} izinleri ayarlanamadı: {ex.Message}", true); }
        }

        private void UpdateIdentityFile(string newId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newId)) return;
                string dir = Path.GetDirectoryName(_identityFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_identityFilePath, newId);
                _hwId = newId;
                POpsHelpers.Log("AGENT", $"Kimlik başarıyla güncellendi: {_hwId}");
            }
            catch { }
        }

        private object GetHardwareDnaInternal()
        {
            bool ramReadable = true, diskSerialReal = true, wmiHealthy = true;
            string uuid = "NULL", biosSn = "NULL", diskSn = "NULL", mac = "NULL", ramSn = "NULL";

            try
            {
                uuid = GetWmiValue("Win32_ComputerSystemProduct", "UUID");
                if (uuid == "-" || uuid == "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF") uuid = "NULL";
                biosSn = GetWmiValue("Win32_BIOS", "SerialNumber");
                if (biosSn == "-" || biosSn.Contains("O.E.M")) biosSn = "NULL";
                diskSn = GetWmiValue("Win32_DiskDrive", "SerialNumber");
                if (diskSn == "-" || string.IsNullOrWhiteSpace(diskSn)) { diskSerialReal = false; diskSn = GetVolumeId(); }
                ramSn = GetRamSerialNumbers();
                if (ramSn == "NULL") ramReadable = false;
                mac = GetMacAddress();
            }
            catch { wmiHealthy = false; }

            return new
            {
                os = GetWmiValue("Win32_OperatingSystem", "Caption"),
                capabilities = new { ram_readable = ramReadable, disk_serial_real = diskSerialReal, wmi_healthy = wmiHealthy },
                hardware = new { uuid, bios_sn = biosSn, disk_sn = diskSn, mac, ram_sn = ramSn }
            };
        }

        private object BuildInventoryInternal()
        {
            try
            {
                return new
                {
                    hw_id = _hwId,
                    hostname = _pcName,
                    cpu = GetWmiValue("Win32_Processor", "Name"),
                    ram = GetTotalRam(),
                    motherboard = GetWmiValue("Win32_BaseBoard", "Product"),
                    gpu = GetWmiValue("Win32_VideoController", "Name"),
                    os_version = GetWmiValue("Win32_OperatingSystem", "Caption"),
                    ip_address = GetLocalIPAddress(),
                    mac_address = GetMacAddress(),
                    disk_info = GetDiskInfo(),
                    dna = _cachedDna
                };
            }
            catch { return null; }
        }

        private async Task SendHardwareInfoAsync()
        {
            if (_cachedInventory == null) return;
            try
            {
                string json = JsonSerializer.Serialize(_cachedInventory);
                using var request = new HttpRequestMessage(HttpMethod.Post, _serverUrl.TrimEnd('/') + $"/api/inventory/{_hwId}")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                AgentCredentials.AddHttpAuth(request, _hwId);
                // Kimlik başlıkları taşıyan istek yönlendirme izlemeyen istemciyle gider (bkz. AgentHttp)
                using var response = await AgentHttp.Client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    POpsHelpers.Log("AGENT", $"Donanım envanteri gönderilemedi: HTTP {(int)response.StatusCode}.", true);
                    return;
                }
                POpsHelpers.Log("AGENT", "Donanım envanteri sunucuya gönderildi.");
                _health.InventoryUploaded();
            }
            catch (Exception ex)
            {
                _health.RecordError("inventory", ex.Message);
                POpsHelpers.Log("AGENT", $"Donanım envanteri gönderilemedi: {ex.Message}", true);
            }
        }

        // Takılan bir WMI sağlayıcısı sorguyu süresiz bekletmesin: bağlantı ve her sonuç için zaman aşımı
        private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(15);

        private static ManagementObjectSearcher WmiQuery(string query) =>
            new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { Timeout = WmiTimeout }),
                new ObjectQuery(query),
                new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true, Rewindable = false });

        private string GetRamSerialNumbers()
        {
            try
            {
                var serials = new List<string>();
                using var searcher = WmiQuery("SELECT SerialNumber FROM Win32_PhysicalMemory");
                foreach (var obj in searcher.Get())
                {
                    string sn = obj["SerialNumber"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(sn) && sn != "Unknown" && sn != "00000000") serials.Add(sn);
                }
                return serials.Count > 0 ? string.Join(",", serials) : "NULL";
            }
            catch { return "NULL"; }
        }

        private string GetVolumeId()
        {
            try
            {
                var drive = new DriveInfo("C");
                if (drive.IsReady)
                {
                    using var process = new Process();
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.Arguments = "/c vol c:";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    foreach (string line in output.Split('\n')) if (line.Contains("-")) return line.Split(' ').Last().Trim();
                }
            }
            catch { }
            return "NULL";
        }

        private string GenerateFallbackHash()
        {
            try
            {
                string raw = GetWmiValue("Win32_ComputerSystemProduct", "UUID") + GetMacAddress();
                using MD5 md5 = MD5.Create();
                byte[] hash = md5.ComputeHash(Encoding.ASCII.GetBytes(raw));
                return "HW-" + BitConverter.ToString(hash).Replace("-", "").Substring(0, 12);
            }
            catch { return "HW-" + Guid.NewGuid().ToString().Substring(0, 12); }
        }

        private string GetWmiValue(string wmiClass, string property)
        {
            try
            {
                using var searcher = WmiQuery($"SELECT {property} FROM {wmiClass}");
                foreach (var obj in searcher.Get()) return obj[property]?.ToString()?.Trim() ?? "-";
            }
            catch { }
            return "-";
        }

        private string GetTotalRam()
        {
            try
            {
                using var searcher = WmiQuery("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get()) if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong bytes)) return (bytes / (1024L * 1024 * 1024)) + " GB";
            }
            catch { }
            return "-";
        }

        private string GetMacAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback) return string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            }
            catch { }
            return "-";
        }

        private string GetLocalIPAddress()
        {
            try
            {
                foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList) if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return ip.ToString();
            }
            catch { }
            return "-";
        }

        private string GetDiskInfo()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var drive in DriveInfo.GetDrives()) if (drive.IsReady && drive.DriveType == DriveType.Fixed) sb.Append($"{drive.Name} {drive.TotalFreeSpace / (1024L * 1024 * 1024)}GB Boş / {drive.TotalSize / (1024L * 1024 * 1024)}GB Toplam | ");
                return sb.ToString().TrimEnd(' ', '|');
            }
            catch { }
            return "-";
        }

        // Ağ karantinası: bkz. NetworkIsolation (eski uygulama güvenlik duvarında hiçbir kural oluşturamıyordu)
        private Task<bool> EnableNetworkIsolationAsync() => NetworkIsolation.EnableAsync(_serverUrl);

        private Task<bool> DisableNetworkIsolationAsync() => NetworkIsolation.DisableAsync();

        private async Task<CommandExecutionResult> ExecuteCommandAsync(string command)
        {
            string tempBatPath = "";
            var stopwatch = Stopwatch.StartNew();
            int exitCode = -1;
            string output;
            try
            {
                tempBatPath = Path.Combine(Path.GetTempPath(), $"pops_task_{Guid.NewGuid():N}.bat");
                await File.WriteAllTextAsync(tempBatPath, "@echo off\r\nchcp 65001 > nul\r\n" + command, new UTF8Encoding(false));

                var processInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{tempBatPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = processInfo };
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cts = new CancellationTokenSource(CommandExecutionPolicy.MaxDuration);
                try { await process.WaitForExitAsync(cts.Token); }
                catch (TaskCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    output = "[HATA]: İşlem 30 dakikadan uzun sürdüğü için zorla sonlandırıldı.";
                    return new CommandExecutionResult(output, exitCode, stopwatch.Elapsed);
                }

                exitCode = process.ExitCode;
                string stdOut = outputBuilder.ToString().Trim();
                string stdErr = errorBuilder.ToString().Trim();

                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stdErr))
                    output = $"[ÇIKIŞ KODU: {process.ExitCode}]\n[HATA]:\n{stdErr}\n[ÇIKTI]:\n{stdOut}";
                else
                    output = string.IsNullOrWhiteSpace(stdOut) ? $"Komut çalıştı (Çıkış: {process.ExitCode}) ancak çıktı üretilmedi." : stdOut;

                return new CommandExecutionResult(output, exitCode, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                return new CommandExecutionResult($"Ajan Hatası: {ex.Message}", exitCode, stopwatch.Elapsed);
            }
            finally
            {
                stopwatch.Stop();
                if (!string.IsNullOrEmpty(tempBatPath) && File.Exists(tempBatPath)) try { File.Delete(tempBatPath); } catch { }
            }
        }

    }

    public sealed class CommandExecutionResult
    {
        public CommandExecutionResult(string output, int exitCode, TimeSpan duration)
        {
            Output = CommandExecutionPolicy.TruncateOutput(output);
            ExitCode = exitCode;
            Duration = duration;
        }

        public string Output { get; }
        public int ExitCode { get; }
        public TimeSpan Duration { get; }
    }

    public sealed class AgentStartupHealth
    {
        private readonly OperationalHealthGate _gate;

        public AgentStartupHealth(bool suppressed, Action<OperationalChecks> writer = null)
        {
            _gate = new OperationalHealthGate(suppressed, writer ?? AgentUpdate.WriteOperationalHealth);
        }

        public void Run(StartupCheck check, Action action) => _gate.Run(check, action);
        public void Mark(StartupCheck check) => _gate.Mark(check);
        public OperationalChecks Snapshot() => _gate.Snapshot();
    }

    public class TrayPipeServer
    {
        private readonly ILogger _logger;
        private readonly string _hwId;
        private readonly HttpClient _http;
        private readonly string _serverUrl;
        private const int MaxPipeMessageBytes = 32 * 1024 * 1024;
        private CancellationTokenSource _cts;
        private NamedPipeServerStream _pipeServer;
        private TaskCompletionSource<bool> _listening;
        public event Action<string> OnMessageReceived = delegate { };
        public event Action<byte[]> OnFrameReceived = delegate { };
        // Tepsi bağlantısı koptuğunda (onaylı Vision oturumu da onunla biter)
        public event Action OnDisconnected = delegate { };
        // Doğrulanmış tepsi bağlandı (servis açılışı, tepsinin yeniden başlaması, oturum değişimi)
        public event Action OnConnected = delegate { };
        private readonly object _writeLock = new object();

        // Boruya yalnızca kurulum klasöründeki tepsi bağlanabilir (bkz. PipeClientVerifier)
        private static readonly string TrayExePath = Path.Combine(AppContext.BaseDirectory, "POpsTray.exe");

        public TrayPipeServer(ILogger logger, string hwId, HttpClient http, string serverUrl)
        {
            _logger = logger; _hwId = hwId; _http = http; _serverUrl = serverUrl;
        }

        public Task Start()
        {
            _cts = new CancellationTokenSource();
            _listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => ListenPipeAsync(_cts.Token));
            return _listening.Task;
        }

        private string _lastPipeError;

        // Bağlı tepsinin oturumundaki kullanıcı (bağlantı yoksa null)
        public string ClientUser { get; private set; }

        public bool IsConnected
        {
            get
            {
                try { return _pipeServer?.IsConnected == true; }
                catch (ObjectDisposedException) { return false; }
            }
        }
        public void Stop() { _cts?.Cancel(); _pipeServer?.Dispose(); }

        public void SendCommandToDesktop(string json)
        {
            var pipe = _pipeServer;
            if (pipe == null || !pipe.IsConnected) return;
            try { WriteFrame(pipe, _writeLock, Encoding.UTF8.GetBytes(json)); }
            catch { }
        }

        // Bir mesaj: 4 bayt uzunluk + içerik, tek parça ve kilit altında yazılır. Komut döngüsü, Vision, zamanlayıcılar
        // ve bypass aynı anda yazabilir; kilitsiz iki mesajın parçaları birbirine karışıp tepsinin okumasını bozardı.
        internal static void WriteFrame(Stream stream, object gate, byte[] payload)
        {
            byte[] frame = new byte[4 + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
            payload.CopyTo(frame, 4);
            lock (gate)
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
        }

        private async Task ListenPipeAsync(CancellationToken token)
        {
            string pipeName = @"POpsTrayPipe";
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ps = new PipeSecurity();
                    // Yalnızca oturum açmış (etkileşimli) kullanıcının tepsi uygulaması bağlanabilir;
                    // ağ ve servis hesapları için Everyone izni kaldırıldı.
                    var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
                    var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                    ps.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
                    ps.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
                    ps.AddAccessRule(new PipeAccessRule(interactive, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
                    // Tepsi borunun sahibini denetler (SYSTEM ya da Administrators; bkz. POps.Shared.PipeOwner). Sahip
                    // belirtecin varsayılanına bırakılmaz. ReadWrite, tepsinin sahibi okuması için ReadPermissions'ı içerir.
                    ps.SetOwner(system);

                    _pipeServer = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
                    _listening.TrySetResult(true);
                    POpsHelpers.Log("PIPE", $"Bekleniyor: {pipeName}");

                    await _pipeServer.WaitForConnectionAsync(token);
                    string rejection = PipeClientVerifier.Verify(_pipeServer.SafePipeHandle, TrayExePath, out uint clientPid);
                    if (rejection != null)
                    {
                        POpsHelpers.Log("PIPE", $"[GÜVENLİK] Tepsi borusuna doğrulanmamış istemci bağlandı, bağlantı kesildi: {rejection}", true);
                        _pipeServer.Disconnect();
                        // Sürekli bağlanıp gerçek tepsiyi dışarıda bırakmaya çalışan istemciyi yavaşlatır
                        await Task.Delay(2000, token);
                        continue;
                    }
                    ClientUser = UserSessionLauncher.SessionUser(UserSessionLauncher.SessionOf((int)clientPid));
                    _lastPipeError = null;
                    POpsHelpers.Log("PIPE", "🟢 Tepsi bağlandı (doğrulandı).");
                    try { OnConnected?.Invoke(); } catch (Exception ex) { POpsHelpers.Log("PIPE", $"Bağlantı sonrası eşitleme başarısız: {ex.Message}", true); }

                    byte[] lBuf = new byte[4];
                    while (_pipeServer.IsConnected && !token.IsCancellationRequested)
                    {
                        int lRead = 0;
                        while (lRead < 4)
                        {
                            int r = await _pipeServer.ReadAsync(lBuf, lRead, 4 - lRead, token);
                            if (r == 0) break;
                            lRead += r;
                        }
                        if (lRead < 4) break;
                        
                        int dLen = BitConverter.ToInt32(lBuf, 0);
                        // Boru hattına oturum açan her kullanıcı yazabilir: sınırsız ya da negatif uzunluk SYSTEM
                        // servisinin belleğini tüketirdi. En büyük meşru mesaj tepsinin gönderdiği ekran karesidir.
                        if (dLen <= 0 || dLen > MaxPipeMessageBytes)
                        {
                            POpsHelpers.Log("PIPE", $"[GÜVENLİK] Geçersiz mesaj uzunluğu ({dLen}); bağlantı kapatıldı.", true);
                            break;
                        }
                        byte[] d = new byte[dLen];
                        int total = 0;
                        while (total < dLen)
                        {
                            int r = await _pipeServer.ReadAsync(d, total, dLen - total, token);
                            if (r == 0) break;
                            total += r;
                        }
                        if (total == dLen) 
                        {
                            if (dLen > 2 && d[0] == 0xFF && d[1] == 0xD8)
                            {
                                OnFrameReceived?.Invoke(d);
                            }
                            else
                            {
                                string msg = Encoding.UTF8.GetString(d);
                                OnMessageReceived?.Invoke(msg);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Boru adı başka bir süreçte kaldıkça 3 sn'de bir aynı hata loglanmaz
                    if (ex.Message != _lastPipeError) POpsHelpers.Log("PIPE", $"Hata: {ex.Message}", true);
                    _lastPipeError = ex.Message;
                    await Task.Delay(3000, token);
                }
                finally
                {
                    _pipeServer?.Dispose();
                    ClientUser = null;
                    OnDisconnected?.Invoke();
                }
            }
        }
    }
}
