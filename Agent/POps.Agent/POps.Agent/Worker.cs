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

        // 🚀 ARTIK SABİT DEĞİL, HELPERS'TAN OKUNACAK
        private string _serverUrl;

        // Sunucu kimliksiz ajanı reddederken WebSocket'i bu kodla kapatır (enforce_agent_auth açıkken)
        private const WebSocketCloseStatus AuthRejectedCloseStatus = (WebSocketCloseStatus)4401;

        private ClientWebSocket _commandWs;
        private ClientWebSocket _visionWs;
        private readonly SemaphoreSlim _wsCommandLock = new(1, 1);
        private readonly SemaphoreSlim _wsVisionLock = new(1, 1);

        private TrayPipeServer _trayPipe;
        private volatile bool _isVisionStreamActive;
        // Uzaktan fare/klavye yalnızca kullanıcının tepsi üzerinden onayladığı (ya da zorunlu oturumda bildirimin
        // gösterildiği) Vision oturumu açıkken uygulanır. Sunucu ele geçirilse bile yerel onay olmadan girdi yok.
        private volatile bool _visionSessionApproved;
        private TaskCompletionSource<byte[]> _thumbnailTcs;


        private object _cachedDna = null;
        private object _cachedInventory = null;

        private AgentPolicy _currentPolicy = new AgentPolicy();
        private bool _fairUseAcknowledged = false;
        private DateTime _lastPolicyFetch = DateTime.MinValue;

        // Karantina (lockdown/unlock/çevrimdışı bypass) ve Windows Update: bkz. QuarantineControl, PatchManager
        private readonly QuarantineControl _quarantine;
        private readonly PatchManager _patches;
        // Ön plandaki uygulamanın süreç adı (tepsiden, yalnızca ad; bkz. ActiveApp). Bilinmiyorsa null.
        private volatile string _activeApp;

        public Worker(ILogger<Worker> logger)
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
            _pcName = Environment.MachineName;
            _httpClient = new HttpClient();

            // 🚀 IP'Yİ CONFIG DOSYASINDAN AL
            _serverUrl = POpsHelpers.GetServerUrl();
            POpsHelpers.Log("AGENT", $"POps Agent Başlatılıyor (Hedef: {_serverUrl})");
            foreach (string configPath in POpsHelpers.ConfigPaths) SecureConfigFile(configPath);

            _quarantine = new QuarantineControl(message => _trayPipe?.SendCommandToDesktop(message), EnableNetworkIsolationAsync, DisableNetworkIsolationAsync);
            _patches = new PatchManager(_serverUrl, () => _hwId);
        }

        // Yavaş olabilen açılış işleri (WMI donanım sorguları, kimlik, güvenli depo). ExecuteAsync bunları arka
        // planda çalıştırır: servisin açılışını ve updater'ın beklediği health.json'u bekletmezler.
        // Kimlik önce kurulur; envanter hw_id'yi ondan alır.
        private void InitializeState()
        {
            _hwId = InitializeIdentity();
            POpsHelpers.Log("AGENT", $"Kimlik Başlatıldı: {_hwId}");

            AgentCredentials.Initialize();
            AgentCredentials.LoadSecret();
            AgentCapabilities.Load();

            _cachedDna = GetHardwareDnaInternal();
            _cachedInventory = BuildInventoryInternal();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Run(InitializeState, stoppingToken);
            AgentUpdate.LogLastResult();

            if (!POpsHelpers.IsSecureServerUrl(_serverUrl))
            {
                await RunWithoutServerAsync(stoppingToken);
                return;
            }

            string baseWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");

            // Start background tasks
            _ = Task.Run(() => PolicyPollingLoop(stoppingToken));

            // Sunucuya bildirimler (yalnızca cihaz secret'ı varken; bkz. AgentHttp): yazılım envanteri (açılıştan
            // kısa süre sonra, sonra 6 saatte bir), günlük Windows Update taraması, oturum açma/kapama
            var sessions = new SessionReporter(_serverUrl, () => _hwId, _pcName);
            sessions.UserChanged += _ => _activeApp = null;
            _ = Task.Run(() => new SoftwareReporter(_serverUrl, () => _hwId).RunAsync(stoppingToken));
            _ = Task.Run(() => _patches.ScheduleLoopAsync(stoppingToken));
            _ = Task.Run(() => sessions.RunAsync(stoppingToken));

            while (!stoppingToken.IsCancellationRequested)
            {
                string commandWsUrl = $"{baseWsUrl}/ws/agent/{_hwId}";

                EnsureWatchDogIsRunning();
                StartTrayPipeServer();
                _commandWs = new ClientWebSocket();
                _commandWs.Options.SetRequestHeader("X-Agent-Version", APP_VERSION);
                string authMode = ApplyAuthHeaders(_commandWs);
                POpsHelpers.Log("AGENT", $"[POps V4] DUAL-SOCKET MİMARİSİ BAŞLATILDI ({APP_VERSION}, kimlik: {authMode})");

                try
                {
                    await _commandWs.ConnectAsync(new Uri(commandWsUrl), stoppingToken);
                    POpsHelpers.Log("AGENT", "[+] Ana Komut Tüneli Kuruldu.");
                    OnCommandChannelConnected();

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
                    }
                }
                catch (Exception ex)
                {
                    POpsHelpers.Log("AGENT", $"[!] Santralle bağlantı koptu: {ex.Message}", true);
                }

                // Reddedilen her bağlantı sunucuda denetim kaydı açar; 5 sn'de bir yeniden denenmez
                bool authRejected = _commandWs.CloseStatus == AuthRejectedCloseStatus;
                if (authRejected)
                    POpsHelpers.Log("AGENT", "[GÜVENLİK] Sunucu ajan kimliğini reddetti (4401): geçerli bir enroll jetonu gerekiyor. 60 sn sonra yeniden denenecek.", true);

                _trayPipe?.Stop();
                await DisconnectVisionTunnelAsync();
                await Task.Delay(authRejected ? 60000 : 5000, stoppingToken);
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
            EnsureWatchDogIsRunning();
            StartTrayPipeServer();
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
            if (!POpsHelpers.IsSecureServerUrl(_serverUrl)) return "yok (şifresiz bağlantı)";

            string secret = AgentCredentials.CurrentSecret ?? AgentCredentials.LoadSecret();
            string enrollToken = AgentCredentials.GetEnrollToken();

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

        private void EnsureWatchDogIsRunning()
        {
            try
            {
                string targetDir = AppDomain.CurrentDomain.BaseDirectory;
                string exePath = Path.Combine(targetDir, "POpsWatchdog.exe");
                if (File.Exists(exePath))
                {
                    // Eğer çalışmıyorsa schtasks ile Session 1'de (BUILTIN\Users) başlat
                    if (Process.GetProcessesByName("POpsWatchdog").Length == 0)
                    {
                        Process p = new Process();
                        p.StartInfo.FileName = "cmd.exe";
                        p.StartInfo.Arguments = $"/c schtasks /create /tn \"POpsWatchdogLauncher\" /tr \"\\\"{exePath}\\\"\" /sc once /st 00:00 /ru \"BUILTIN\\Users\" /it /f >nul 2>&1 & schtasks /run /tn \"POpsWatchdogLauncher\" >nul 2>&1 & schtasks /delete /tn \"POpsWatchdogLauncher\" /f >nul 2>&1";
                        p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                        p.StartInfo.CreateNoWindow = true;
                        p.Start();
                        POpsHelpers.Log("AGENT", "POpsWatchDog etkileşimli oturumda (Session 1+) başlatıldı.");
                    }
                }
            }
            catch { }
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
                        
                        if (!string.IsNullOrWhiteSpace(policy.fair_use_text) && !_fairUseAcknowledged)
                        {
                            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(policy.fair_use_text));
                            _trayPipe?.SendCommandToDesktop($"SHOW_FAIR_USE:{b64}");
                        }
                    }
                }
                catch { }
                await Task.Delay(60000, token); // Poll every minute
            }
        }

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

            _trayPipe.Start();
        }

        // Çevrimdışı bypass kodu: geçerliyse sunucunun unlock'u ile aynı yol (kilit ekranı kapanır, yalıtım kalkar).
        // Sunucuya ulaşılabiliyorsa kullanım denetim kaydına da düşer.
        private async Task HandleBypassAttemptAsync(string token)
        {
            try
            {
                var result = await _quarantine.HandleBypassAsync(token, _hwId, AgentCredentials.GetBypassSecret(), DateTime.Now);
                if (result != OfflineBypass.Result.Accepted) return;
                string hwId = _hwId;
                await AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/logs/", hwId), hwId, new AgentLogPayload
                {
                    LogType = "Security",
                    Message = "Çevrimdışı bypass kodu ile kilit ekranı ve karantina kaldırıldı",
                    EventType = "agent.offline_bypass",
                    Category = "security",
                    Action = "offline_bypass",
                    RiskLevel = "medium",
                }, "Bypass denetim kaydı");
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Offline Bypass işlenemedi: {ex.Message}", true); }
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
            var newWs = new ClientWebSocket();
            // Sunucu enforce_agent_auth açıkken kimliksiz Vision tünelini (sahte ekran görüntüsü) reddeder
            newWs.Options.SetRequestHeader("X-Agent-Version", APP_VERSION);
            ApplyAuthHeaders(newWs);
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
                _isVisionStreamActive = false;
                newWs.Dispose();
            }
        }

        private async Task DisconnectVisionTunnelAsync()
        {
            _isVisionStreamActive = false;
            _visionSessionApproved = false;
            var ws = Interlocked.Exchange(ref _visionWs, null);
            if (ws != null) { try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Yayın Kesildi", CancellationToken.None); } catch { } ws.Dispose(); }
        }

        // Mesaj boyu sınırları (parçalı mesajlar EndOfMessage'a kadar birleştirilir; bkz. WebSocketMessages)
        private const int MaxCommandMessageBytes = 8 * 1024 * 1024;
        private const int MaxVisionMessageBytes = 1024 * 1024;

        // Uzaktan fare/klavye olayı (input_type taşıyan remote_input). Ekran önizlemesi ve FPS ayarı girdi değildir.
        private static bool IsInputEvent(JsonElement root) => root.TryGetProperty("input_type", out _);

        private bool InputAllowed() => _visionSessionApproved && _isVisionStreamActive;

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
                        if (!AgentCapabilities.VisionEnabled) { await DenyCapabilityAsync("vision", "remote_input"); continue; }
                        if (IsInputEvent(root) && !InputAllowed()) { await DenyCapabilityAsync("consent", "remote_input"); continue; }
                        _trayPipe?.SendCommandToDesktop(message);
                    }
                }
            }
            catch (WebSocketMessages.TooLargeException ex) { POpsHelpers.Log("AGENT", $"[GÜVENLİK] Vision tüneli kapatıldı: {ex.Message}.", true); }
            catch { }
            finally { await DisconnectVisionTunnelAsync(); }
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
                        if (!AgentCapabilities.VisionEnabled)
                        {
                            await DenyCapabilityAsync("vision", string.IsNullOrEmpty(act) ? "remote_input" : act);
                            continue;
                        }
                        // Sunucu ele geçirilse bile kullanıcının onayladığı bir oturum yoksa fare/klavye uygulanmaz
                        if (IsInputEvent(root) && !InputAllowed())
                        {
                            await DenyCapabilityAsync("consent", "remote_input");
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
                        if (action == "execute" && !AgentCapabilities.TerminalEnabled)
                        {
                            // Görev "Running"de asılı kalmasın diye sonuç olarak da bildirilir
                            int tid = root.GetProperty("task_id").GetInt32();
                            await SendCommandMessageAsync(new { type = "result", pc_name = _hwId, task_id = tid, output = "[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı (yetenek politikası); komut çalıştırılmadı." });
                            await DenyCapabilityAsync("terminal", "execute", tid);
                        }
                        else if (action == "execute")
                        {
                            string cmd = root.GetProperty("script_path").GetString();
                            int tid = root.GetProperty("task_id").GetInt32();
                            POpsHelpers.Log("AGENT", $"Uzaktan komut çalıştırılıyor (TaskID: {tid})");
                            _ = Task.Run(async () =>
                            {
                                string outpt = await ExecuteCommandAsync(cmd);
                                var res = new { type = "result", pc_name = _hwId, output = outpt, task_id = tid };
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
                            await ConnectVisionTunnelAsync(stoppingToken); 
                            int fps = root.TryGetProperty("fps", out var fProp) ? (fProp.ValueKind == JsonValueKind.Number ? fProp.GetInt32() : 2) : 2;
                            if (_isVisionStreamActive) _trayPipe?.SendCommandToDesktop($"START_CAPTURE:{fps}");
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
                        else if (action == "lockdown")
                        {
                            string reason = root.TryGetProperty("reason", out var rProp) && rProp.ValueKind == JsonValueKind.String ? rProp.GetString() : null;
                            await _quarantine.LockdownAsync(reason);
                        }
                        else if (action == "unlock") await _quarantine.UnlockAsync("server");
                        // Windows Update: arka planda yürür, bu döngüyü bekletmez (bkz. PatchManager)
                        else if (action == "scan_updates") _patches.RequestScan();
                        else if (action == "install_updates")
                        {
                            string scope = root.TryGetProperty("scope", out var scProp) && scProp.ValueKind == JsonValueKind.String ? scProp.GetString() : null;
                            _patches.RequestInstall(scope);
                        }
                        else if (action == "start_vision_session") { _trayPipe?.SendCommandToDesktop(message); }
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

        private async Task SendHeartbeatAsync(CancellationToken token)
        {
            // Ön plandaki uygulamanın adı (tepsiden); pencere başlığı gönderilmez
            string currentApp = _activeApp ?? "-";

            var statusPayload = new
            {
                hw_id = _hwId,
                hostname = _pcName,
                lab_name = "Atanmamis_Cihazlar",
                status = "Online",
                active_window = currentApp,
                dna_payload = _cachedDna
            };

            string json = JsonSerializer.Serialize(statusPayload);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _wsCommandLock.WaitAsync(token);
            try { if (_commandWs != null && _commandWs.State == WebSocketState.Open) await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token); }
            finally { _wsCommandLock.Release(); }
        }

        // Sunucunun "set_capabilities" isteği: yalnızca kapatma uygulanır (bkz. AgentCapabilities). Vision kapandıysa
        // süren yayın hemen durdurulur. Son durum sunucuya "capabilities" olarak bildirilir.
        private async Task HandleSetCapabilitiesAsync(JsonElement request)
        {
            AgentCapabilities.ApplyServerRequest(request);
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

        private async Task DenyCapabilityAsync(string capability, string action, int? taskId = null)
        {
            string key = $"{capability}/{action}";
            lock (_lastDenialNotice)
            {
                if (taskId == null && _lastDenialNotice.TryGetValue(key, out DateTime last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
                _lastDenialNotice[key] = DateTime.UtcNow;
            }
            POpsHelpers.Log("POLICY", $"[GÜVENLİK] {action} reddedildi: {capability} bu cihazda kapalı (yetenek politikası).", true);
            var notice = new Dictionary<string, object> { ["type"] = "capability_denied", ["capability"] = capability, ["action"] = action };
            if (taskId != null) notice["task_id"] = taskId.Value;
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
                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    POpsHelpers.Log("AGENT", $"Donanım envanteri gönderilemedi: HTTP {(int)response.StatusCode}.", true);
                    return;
                }
                POpsHelpers.Log("AGENT", "Donanım envanteri sunucuya gönderildi.");
            }
            catch { }
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
        private Task EnableNetworkIsolationAsync() => NetworkIsolation.EnableAsync(_serverUrl);

        private Task DisableNetworkIsolationAsync() => NetworkIsolation.DisableAsync();

        private async Task<string> ExecuteCommandAsync(string command)
        {
            string tempBatPath = "";
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

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                try { await process.WaitForExitAsync(cts.Token); }
                catch (TaskCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    return "[HATA]: İşlem 30 dakikadan uzun sürdüğü için zorla sonlandırıldı.";
                }

                string stdOut = outputBuilder.ToString().Trim();
                string stdErr = errorBuilder.ToString().Trim();
                try { File.Delete(tempBatPath); } catch { }

                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stdErr))
                    return $"[ÇIKIŞ KODU: {process.ExitCode}]\n[HATA]:\n{stdErr}\n[ÇIKTI]:\n{stdOut}";

                return string.IsNullOrWhiteSpace(stdOut) ? $"Komut çalıştı (Çıkış: {process.ExitCode}) ancak çıktı üretilmedi." : stdOut;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(tempBatPath) && File.Exists(tempBatPath)) try { File.Delete(tempBatPath); } catch { }
                return $"Ajan Hatası: {ex.Message}";
            }
        }

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
        public event Action<string> OnMessageReceived = delegate { };
        public event Action<byte[]> OnFrameReceived = delegate { };
        // Tepsi bağlantısı koptuğunda (onaylı Vision oturumu da onunla biter)
        public event Action OnDisconnected = delegate { };

        // Boruya yalnızca kurulum klasöründeki tepsi bağlanabilir (bkz. PipeClientVerifier)
        private static readonly string TrayExePath = Path.Combine(AppContext.BaseDirectory, "POpsTray.exe");

        public TrayPipeServer(ILogger logger, string hwId, HttpClient http, string serverUrl)
        {
            _logger = logger; _hwId = hwId; _http = http; _serverUrl = serverUrl;
        }

        public void Start() { _cts = new CancellationTokenSource(); Task.Run(() => ListenPipeAsync(_cts.Token)); }
        public void Stop() { _cts?.Cancel(); _pipeServer?.Dispose(); }

        public void SendCommandToDesktop(string json)
        {
            if (_pipeServer == null || !_pipeServer.IsConnected) return;
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(json);
                byte[] len = BitConverter.GetBytes(b.Length);
                _pipeServer.Write(len, 0, 4);
                _pipeServer.Write(b, 0, b.Length);
                _pipeServer.Flush();
            }
            catch { }
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

                    _pipeServer = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
                    POpsHelpers.Log("PIPE", $"Bekleniyor: {pipeName}");

                    await _pipeServer.WaitForConnectionAsync(token);
                    string rejection = PipeClientVerifier.Verify(_pipeServer.SafePipeHandle, TrayExePath);
                    if (rejection != null)
                    {
                        POpsHelpers.Log("PIPE", $"[GÜVENLİK] Tepsi borusuna doğrulanmamış istemci bağlandı, bağlantı kesildi: {rejection}", true);
                        _pipeServer.Disconnect();
                        // Sürekli bağlanıp gerçek tepsiyi dışarıda bırakmaya çalışan istemciyi yavaşlatır
                        await Task.Delay(2000, token);
                        continue;
                    }
                    POpsHelpers.Log("PIPE", "🟢 Tepsi bağlandı (doğrulandı).");

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
                    POpsHelpers.Log("PIPE", $"Hata: {ex.Message}", true);
                    await Task.Delay(3000, token);
                }
                finally
                {
                    _pipeServer?.Dispose();
                    OnDisconnected?.Invoke();
                }
            }
        }
    }
}
