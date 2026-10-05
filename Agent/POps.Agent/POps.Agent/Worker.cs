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
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CA1416
#nullable disable

namespace POpsAgent
{
    [SupportedOSPlatform("windows")]
    public class Worker : BackgroundService
    {
        // Sürüm kök VERSION dosyasından gelir (Directory.Build.props -> assembly). Elle güncellenmez.
        public static readonly string AppVersion = POpsHelpers.AppVersion;

        private readonly ILogger<Worker> _logger;
        private readonly string _pcName;
        private string _hwId;
        // C:\POpsData\identity.key (testlerde geçici klasör)
        private readonly string _identityFilePath = AgentUpdate.IdentityPath;
        private readonly HttpClient _httpClient;
        private readonly AgentStartupHealth _startupHealth;
        private readonly AgentHealthTelemetry _health = new AgentHealthTelemetry();
        // Uzaktan komutlar (iptal ve servis durması işlemi sonlandırır)
        private CommandRunner _commandRunner = new CommandRunner();
        private Task _slowInitialization;

        // 🚀 ARTIK SABİT DEĞİL, HELPERS'TAN OKUNACAK
        private string _serverUrl;

        private ClientWebSocket _commandWs;
        private bool _commandUsesDeviceSecret;
        private ClientWebSocket _visionWs;
        private readonly SemaphoreSlim _wsCommandLock = new(1, 1);
        private readonly SemaphoreSlim _wsVisionLock = new(1, 1);
        // Vision v2: ikili kareler, görüntüleyici denetimi, pano (bkz. VisionRelay)
        private readonly VisionRelay _visionRelay;

        private TrayPipeServer _trayPipe;
        private volatile bool _isVisionStreamActive;
        // Uzaktan fare/klavye yalnızca kullanıcının tepsi üzerinden onayladığı (ya da zorunlu oturumda bildirimin
        // gösterildiği) Vision oturumu açıkken uygulanır. Sunucu ele geçirilse bile yerel onay olmadan girdi yok.
        private volatile bool _visionSessionApproved;
        private string _visionSessionId;
        private string _visionRequestedBy;
        private string _visionReason;
        private bool _visionUserApproved;
        private bool _visionAuditActive;
        private bool _pendingVisionMandatory;
        // Bu oturumda tepsinin oturum bildirimi kuruldu (oturum bilgisayar kilitliyken başladı; bkz. OnVisionStartedLocked)
        private bool _visionNoticeArmed;
        private TaskCompletionSource<byte[]> _thumbnailTcs;


        private object _cachedDna;
        private object _cachedInventory;

        private AgentPolicy _currentPolicy = new AgentPolicy();
        private bool _fairUseAcknowledged;

        // Karantina (lockdown/unlock/çevrimdışı bypass) ve Windows Update: bkz. QuarantineControl, PatchManager
        private QuarantineControl _quarantine;
        // DNS eşiğindeki otomatik karantina ve DNS hataları (bkz. BindDnsPolicyMonitor)
        private readonly Action<string> _dnsQuarantine;
        private readonly Action<string> _dnsErrorReporter;
        private readonly PatchManager _patches;
        // Yardım masası: tepsinin "Sorun bildir" / "Taleplerim" istekleri (bkz. Helpdesk)
        private readonly Helpdesk _helpdesk;
        // "Etkinlik geçmişim": yöneticilerin bu cihazda yaptığı işlemler (bkz. ActivityHistory)
        private readonly ActivityHistory _activity;
        // Sunucunun duyurduğu özellikler (server_info; 15 sn kuralı): update_result ve görev sonucu onayı ortak kullanır
        internal ServerHandshake Handshake { get; } = new ServerHandshake();
        // update_result: sunucu onay destekliyorsa onaya kadar saklanır (bkz. UpdateResultReporter)
        internal UpdateResultReporter UpdateResults { get; }
        // Görev sonuçları: sunucu result_ack destekliyorsa onaya kadar diskte (bkz. ResultSpool)
        internal ResultSpool Results { get; }
        // Görev sonuçlarının gönderimi ve kapalı yetenek/modül reddi (bkz. ResultOutbox, CapabilityGate)
        private readonly ResultOutbox _outbox;
        private readonly CapabilityGate _gate;
        // Ön plandaki uygulamanın süreç adı (tepsiden, yalnızca ad; bkz. ActiveApp). Bilinmiyorsa null.
        private volatile string _activeApp;
        // Cihaz anahtarının donanıma bağı (hw.bind) ve kopyalanmış kurulum denetimi (bkz. HardwareBinding)
        internal HardwareBinding Binding { get; set; }
        // Yazılım envanteri (yeni secret alınınca son gönderim unutulur)
        private SoftwareReporter _software;
        private bool _cloneRejectedAudited;
        // Uzaktan güç işlemleri ve kullanıcı mesajları (bkz. PowerActions, UserMessages)
        internal PowerActions Power { get; }
        internal UserMessages Messages { get; }

        public Worker(ILogger<Worker> logger) : this(logger, new AgentStartupHealth(false)) { }

        public Worker(ILogger<Worker> logger, AgentStartupHealth startupHealth)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("POpsV", StringComparer.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\n========================================");
                Console.WriteLine($" POps Agent - Sürüm: {AppVersion}");
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
            // Yapılandırma okunamadıysa adres son çaredir; sorun açılışta Olay Günlüğüne yazılır, tepside gösterilir
            (_serverUrl, ConfigProblem) = POpsHelpers.ResolveServerUrl();
            POpsHelpers.Log("AGENT", $"POps Agent Başlatılıyor (Hedef: {_serverUrl})");
            foreach (string configPath in POpsHelpers.ConfigPaths) HardwareInfo.SecureConfigFile(configPath);

            _quarantine = new QuarantineControl(message => _trayPipe?.SendCommandToDesktop(message),
                EnableNetworkIsolationAsync, DisableNetworkIsolationAsync, audit: LocalAudit.Write);
            _dnsErrorReporter = message => _health.RecordError("dns", message);
            // DNS eşiğindeki otomatik karantina da kilit ekranı + yalıtım yolundan geçer (bkz. AutoQuarantineAsync)
            _dnsQuarantine = reason => _ = AutoQuarantineAsync(reason);
            _patches = new PatchManager(_serverUrl, () => _hwId);
            // Talebin sahibi konsoldaki değil, isteği yapan tepsinin oturumundaki kullanıcı (hızlı kullanıcı değiştirme, RDP)
            _helpdesk = new Helpdesk(_serverUrl, () => _hwId, () => _trayPipe?.ClientUser, message => _trayPipe?.SendCommandToDesktop(message));
            _activity = new ActivityHistory(_serverUrl, () => _hwId, message => _trayPipe?.SendCommandToDesktop(message));
            UpdateResults = new UpdateResultReporter(Handshake);
            // Önceki çalışmadan onay bekleyen sonuçlar okunur
            Results = new ResultSpool(SecureStore.PathOf(ResultSpool.FileName));
            _outbox = new ResultOutbox(Handshake, Results, TrySendCommandMessageAsync);
            _gate = new CapabilityGate(TrySendCommandMessageAsync, AgentModules.IsEnabled, TimeProvider.System);
            _visionRelay = new VisionRelay(SendVisionBinaryAsync, SendVisionTextAsync, ToTray);
            Power = new PowerActions(SendTaskResultAsync, taskId => DenyCapabilityAsync(PowerActions.Capability, PowerActions.ActionName, taskId),
                ToTray, TrayInConsoleSession);
            Messages = new UserMessages(SendTaskResultAsync, taskId => DenyCapabilityAsync(UserMessages.Capability, UserMessages.ActionName, taskId),
                ToTray, TrayConnected);
            Binding = new HardwareBinding(_identityFilePath,
                () => (HardwareInfo.GetWmiValue("Win32_ComputerSystemProduct", "UUID"), HardwareInfo.GetWmiValue("Win32_BIOS", "SerialNumber")));
            Dispatcher = BuildDispatcher();
        }

        // Yavaş olabilen açılış işleri (WMI donanım sorguları, kimlik, güvenli depo). ExecuteAsync bunları arka
        // planda çalıştırır: servisin açılışını ve updater'ın beklediği health.json'u bekletmezler.
        // Kimlik önce kurulur; envanter hw_id'yi ondan alır.
        private void InitializeCoreState()
        {
            // İlk görevden önce: önceki çalışmadan (çökme) kalmış pops_task_*.bat dosyaları (yönetici komutu içerebilir)
            CommandRunner.CleanupStaleTaskFiles(_commandRunner.TaskDirectory);
            // Kopyalanmış kurulum, kimlik ve secret okunmadan önce denetlenir: anahtar bu donanıma ait değilse kenara alınır
            BindingVerdict binding = CheckHardwareBinding();
            _startupHealth.Run(StartupCheck.Identity, () => _hwId = InitializeIdentity());
            POpsHelpers.Log("AGENT", $"Kimlik Başlatıldı: {_hwId}");

            _startupHealth.Run(StartupCheck.Credentials, () =>
            {
                AgentCredentials.Initialize();
                AgentCredentials.LoadSecret();
            });
            ApplyHardwareBinding(binding);
            _startupHealth.Run(StartupCheck.Capabilities, AgentCapabilities.Load);
            // Karantina yeniden başlatmadan sonra sürüyorsa Ctrl+Alt+Del seçenekleri yeniden kapatılır; sürmüyorsa kalıntı temizlenir
            KioskMode.Sync(_quarantine.IsLocked);
            if (_quarantine.IsLocked) LocalAudit.Write(LocalAudit.QuarantineStarted("açılış"));
            // Kurum sertifikası (server-ca.pem) varsa sunucu yalnızca onunla doğrulanır; kip loglanır
            ServerTrust.Reload();
        }

        private BindingVerdict CheckHardwareBinding()
        {
            try { return Binding.CheckOnStartup(); }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"Donanım bağı denetlenemedi: {ex.Message}", true);
                return BindingVerdict.Unreadable;
            }
        }

        // Klon: yerel denetim kaydı (kimlik donanımdan yeniden türetildi); dosyası kenara alınan görev sonuçları bellekten
        // de bırakılır. Donanımın bir kısmı değişti: Olay Günlüğüne uyarı (açılış başına bir kez). Aynı donanım: dondurma
        // yazılımı C:'yi geri aldıysa identity.key anahtarın verildiği kimliğe döner. Bağ yoksa ve secret varsa (0.1.14 ve
        // önceki kurulum) bugünkü donanım yazılır: ilk kullanımda güven.
        internal void ApplyHardwareBinding(BindingVerdict verdict)
        {
            try
            {
                if (verdict == BindingVerdict.Clone)
                {
                    int dropped = Results.Discard();
                    if (dropped > 0) POpsHelpers.Log("AGENT", $"Asıl cihazın onay bekleyen {dropped} görev sonucu gönderilmeyecek (klon klasöründe).", true);
                    bool token = AgentCredentials.GetEnrollToken() != null;
                    LocalAudit.Write(LocalAudit.CloneDetected(Binding.PreviousHwId, _hwId, Binding.CloneFolder, Binding.MovedFiles, token));
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] Kopyalanmış kurulum: cihaz anahtarı bu donanıma ait değil. {string.Join(", ", Binding.MovedFiles)} "
                        + $"{Binding.CloneFolder} klasörüne taşındı; kimlik {Binding.PreviousHwId ?? "(yok)"} -> {_hwId}. "
                        + (token ? "Enroll jetonuyla yeni cihaz olarak kaydolunacak." : "Enroll jetonu yok: cihaz kayıtsız kalacak, yönetici jeton vermeli."), true);
                }
                else if (verdict == BindingVerdict.Inconclusive)
                    LocalAudit.Write(LocalAudit.HardwarePartlyChanged(Binding.ChangedParts, Binding.SameParts));
                else if (verdict == BindingVerdict.Match)
                {
                    string bound = Binding.BoundHwId;
                    if (bound != null && bound.StartsWith("HW-", StringComparison.Ordinal) && bound != _hwId)
                    {
                        POpsHelpers.Log("AGENT", $"Kimlik dosyası anahtarın verildiği kimlikten farklı ({_hwId}); {bound} geri yükleniyor.", true);
                        UpdateIdentityFile(bound);
                    }
                }
                else if (verdict == BindingVerdict.Missing && AgentCredentials.CurrentSecret != null)
                    Binding.Bind(_hwId, "ilk kullanımda güven");
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Donanım bağı işlenemedi: {ex.Message}", true); }
        }

        private void InitializeSlowState()
        {
            try
            {
                _cachedDna = HardwareInfo.GetHardwareDnaInternal();
                _cachedInventory = BuildInventoryInternal();
            }
            catch (Exception ex)
            {
                // Hata burada kalır: bağlantı döngüsü bu görevi bekler, istisna her yeniden bağlanışta tekrar fırlamasın
                _health.RecordError("inventory", ex.Message);
                POpsHelpers.Log("AGENT", $"Donanım bilgisi toplanamadı: {ex.Message}", true);
            }
        }

        // C:\POpsLogs: 30 günden eski loglar ve 200 MB'ı aşan en eskiler silinir; bugünün logu kalır
        internal static (int Deleted, long FreedBytes) ApplyLogRetention(DateTime nowUtc)
        {
            if (POpsHelpers.LogsToUserProfile) return (0, 0);
            var result = LogRetention.Apply(POpsHelpers.LogDirectory, LogRetention.MachinePatterns, nowUtc, POpsHelpers.LogFilePath(nowUtc.ToLocalTime()));
            if (result.Deleted > 0)
                POpsHelpers.Log("AGENT", $"Log saklama: {result.Deleted} eski log silindi ({result.FreedBytes / (1024.0 * 1024.0):0.0} MB boşaldı; sınır {LogRetention.MaxAge.TotalDays:0} gün, {LogRetention.MaxTotalBytes / (1024 * 1024)} MB).");
            return result;
        }

        private static async Task LogRetentionLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { ApplyLogRetention(DateTime.UtcNow); }
                catch (Exception ex) { POpsHelpers.Log("AGENT", $"Log saklama başarısız: {ex.Message}", true); }
                try { await Task.Delay(TimeSpan.FromDays(1), token); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Yapılandırma sorunu (appsettings.json okunamadı, ServerUrl yok ya da geçersiz); null: sorun yok
        internal string ConfigProblem { get; private set; }

        // Klasör ayarının sorunu (LogDirectory / DataDirectory geçersiz ya da kilitlenemedi; varsayılan klasör kullanılıyor,
        // bkz. AgentDirectories); null: sorun yok
        internal string FolderProblem { get; set; } = AgentDirectories.Problem;

        internal void ReportConfigProblem()
        {
            if (ConfigProblem != null)
            {
                POpsHelpers.Log("AGENT", $"[HATA] Yapılandırma okunamadı: {ConfigProblem}. Sunucu adresi olarak {_serverUrl} kullanılıyor; ajan yönetilemez durumda.", true);
                LocalAudit.Write(LocalAudit.ConfigUnreadable(ConfigProblem, _serverUrl));
            }
            // Ajan varsayılan klasörle yönetilmeye devam eder: tepside gösterilmez, yalnızca log ve Olay Günlüğü
            if (FolderProblem != null)
            {
                POpsHelpers.Log("AGENT", $"[HATA] Yapılandırma: {FolderProblem}.", true);
                LocalAudit.Write(LocalAudit.ConfigUnreadable(FolderProblem, _serverUrl));
            }
        }

        // Tepsi uyarı simgesi ve "yapılandırma okunamadı" gösterir (bkz. POpsTray CONFIG_ERROR)
        internal string ConfigErrorMessage() =>
            ConfigProblem == null ? null : "CONFIG_ERROR:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ConfigProblem));

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            BindDnsPolicyMonitor();
            ReportConfigProblem();
            // Tepsi ve watchdog kullanıcı oturumunda yoksa başlatılır (kurulum/güncelleme sonrası, karantinada kilit ekranı).
            // Yavaş WMI açılışını beklemez.
            _ = Task.Run(() => new UserSessionApps().RunAsync(stoppingToken), stoppingToken);
            // Log saklama: açılışta ve günde bir (bkz. LogRetention)
            _ = Task.Run(() => LogRetentionLoopAsync(stoppingToken), stoppingToken);

            await Task.Run(InitializeCoreState, stoppingToken);
            // Sınav modu sunucuya ulaşılamasa da süresinde biter; uygulama engeli ve izin listesi yenilemesi burada
            _ = Task.Run(() => ExamLoopAsync(stoppingToken), stoppingToken);
            _slowInitialization = Task.Run(InitializeSlowState, stoppingToken);
            // Eş önbelleği: açılışta ve dakikada bir süre/yetenek/karantina denetimi, paket varken sunum (bkz. PeerCache)
            _ = Task.Run(() => PeerCache.RunAsync(stoppingToken), stoppingToken);
            AgentUpdate.LogLastResult();
            // Güncelleme sürmüyorsa önceki çalışmadan kalan aşama dosyası silinir
            AgentUpdate.CleanupStaleProgress();

            if (!POpsHelpers.IsSecureServerUrl(_serverUrl))
            {
                EnsureTrayPipeServer();
                _startupHealth.Mark(StartupCheck.Loop);
                await RunWithoutServerAsync(stoppingToken);
                return;
            }

            string baseWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");

            // Start background tasks
            _ = Task.Run(() => PolicyPollingLoop(stoppingToken), stoppingToken);

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
            _software = new SoftwareReporter(_serverUrl, () => _hwId, _health.InventoryUploaded, error => _health.RecordError("inventory", error));
            _ = Task.Run(() => _software.RunAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => _patches.ScheduleLoopAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => sessions.RunAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => _helpdesk.PollLoopAsync(stoppingToken, () => _trayPipe?.IsConnected == true), stoppingToken);
            // Karantinada sunucunun adresi değişirse izin listesi yenilenir (bkz. NetworkIsolation)
            _ = Task.Run(() => IsolationRefreshLoopAsync(stoppingToken), stoppingToken);

            // Son sağlam bağlantıdan beri art arda başarısız bağlantı sayısı (bkz. ReconnectBackoff)
            int reconnectAttempt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                string commandWsUrl = $"{baseWsUrl}/ws/agent/{_hwId}";

                // Tepsi borusu bir kez açılır ve sunucu bağlantısından bağımsız açık kalır (bkz. OnCommandConnectionLostAsync)
                EnsureTrayPipeServer();
                _commandWs = new ClientWebSocket();
                _commandWs.Options.RemoteCertificateValidationCallback = ServerTrust.WebSocketCallback(new Uri(commandWsUrl));
                _commandWs.Options.SetRequestHeader("X-Agent-Version", AppVersion);
                _commandWs.Options.SetRequestHeader(AgentFeatures.HeaderName, AgentFeatures.Header);
                string authMode = ApplyAuthHeaders(_commandWs);
                POpsHelpers.Log("AGENT", $"[POps V4] DUAL-SOCKET MİMARİSİ BAŞLATILDI ({AppVersion}, kimlik: {authMode})");
                _startupHealth.Mark(StartupCheck.Loop);

                try
                {
                    await _commandWs.ConnectAsync(new Uri(commandWsUrl), stoppingToken);
                    POpsHelpers.Log("AGENT", "[+] Ana Komut Tüneli Kuruldu.");
                    // Sunucunun sonuçları onaylayıp onaylamadığı her bağlantıda yeniden öğrenilir (server_info)
                    OnCommandSocketOpened();
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
                        await ForwardUpdateProgressAsync();
                        await ReportUpdateResultAsync(stoppingToken);
                        await FlushPendingResultsAsync();
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
                // Reddedilen her bağlantı sunucuda denetim kaydı açar; kimlik reddinde en az 60 sn, kopya reddinde
                // (4409: bu kimlik başka bir bilgisayarda bağlı) en az 10 dk beklenir.
                var rejection = ReconnectBackoff.FromCloseStatus((int?)_commandWs.CloseStatus);
                bool authRejected = rejection == ReconnectBackoff.Rejection.Auth;
                TimeSpan wait = ReconnectBackoff.Delay(reconnectAttempt, rejection, Random.Shared);
                reconnectAttempt = ReconnectBackoff.NextAttempt(reconnectAttempt);
                // Karantinada art arda 3 bağlantı hatası: sunucunun adresi değişmiş olabilir, izin listesi beklemeden yenilenir
                if (reconnectAttempt == NetworkIsolation.RefreshAfterFailures && NetworkIsolation.IsActive)
                    _ = Task.Run(() => NetworkIsolation.RefreshServerAddressesAsync(_serverUrl, "art arda 3 bağlantı hatası"));
                if (authRejected)
                {
                    LocalAudit.Write(LocalAudit.AuthenticationRejected("command"));
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] Sunucu ajan kimliğini reddetti (4401): geçerli bir enroll jetonu gerekiyor. {wait.TotalSeconds:0} sn sonra yeniden denenecek.", true);
                }
                else if (rejection == ReconnectBackoff.Rejection.Clone)
                    OnCloneRejected(wait);
                else
                    POpsHelpers.Log("AGENT", $"Sunucuya {wait.TotalSeconds:0.0} sn sonra yeniden bağlanılacak.");

                await OnCommandConnectionLostAsync();
                await Task.Delay(wait, stoppingToken);
            }
        }

        // Sunucu bağlantısı koptu. Tepsi borusu açık kalır: sunucuya ulaşılamazken de çevrimdışı bypass kodu, kilit ekranı
        // ve yardım masası mesajları servise ulaşmalı. 0.1.22'ye kadar boru her kopuşta kapatılıp ancak bir sonraki
        // bağlanma denemesinde açılıyordu; geri çekilme beklemesinde (60 sn'ye kadar, 4401'de 2 dk, 4409'da 11 dk) tepsiye
        // yazılan kod sessizce kayboluyordu. Uzaktan izleme ve girdi biter: tepsi yakalamayı durdurur, basılı kalan uzak
        // tuşları bırakır (eskiden borunun kapanması bunu sağlıyordu).
        internal async Task OnCommandConnectionLostAsync()
        {
            bool visionActive = _isVisionStreamActive || _visionSessionApproved || _visionWs != null;
            _visionSessionApproved = false;
            if (visionActive) _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
            await DisconnectVisionTunnelAsync();
        }

        // ------------------------------------------------------------------ dosya aktarımı (bkz. FileTransfer)
        // Emir doğrulanır ve iş arka planda yürür (komut döngüsünü bekletmez); sonuç file_result ile bildirilir.
        // Yetenek kapalıysa yalnızca capability_denied gider (transfer_id ile; sunucu aktarımı ondan "rejected" yapar).
        // transfer_id eksik ya da geçersizse file_result gönderilmez (sunucu bilmediği aktarımı yok sayar), yalnızca loglanır.
        internal async Task HandleFileTransferAsync(string action, JsonElement root, CancellationToken token)
        {
            string transferId = FileTransfer.TransferIdOf(root);
            if (!AgentCapabilities.FilesEnabled)
            {
                await DenyCapabilityAsync("files", action, transferId: transferId);
                return;
            }
            if (transferId == null)
            {
                POpsHelpers.Log("FILES", $"{action} yok sayıldı: transfer_id eksik ya da geçersiz.", true);
                return;
            }
            if (action == "file_push")
            {
                if (!FileTransfer.TryParsePush(root, _serverUrl, out FileTransfer.PushRequest push, out string error))
                {
                    POpsHelpers.Log("FILES", $"Dosya gönderimi reddedildi ({transferId}): {error}.", true);
                    await SendCommandMessageAsync(FileTransfer.Result(transferId, "rejected", detail: error));
                    return;
                }
                FileTransferTask = Task.Run(async () =>
                {
                    var (outcome, path, detail) = await FileTransfer.PushAsync(push, _hwId, DateTime.Now, token);
                    if (outcome == "done")
                    {
                        LocalAudit.Write(LocalAudit.FilePushed(push.TransferId, path, push.Size, push.Sha256, push.Reason));
                        POpsHelpers.Log("FILES", $"Yönetici dosya gönderdi: {path} ({push.Size} bayt).");
                        ToTray("FILE_PUSHED:" + Path.GetFileName(path));
                    }
                    else POpsHelpers.Log("FILES", $"Dosya gönderimi tamamlanmadı ({push.TransferId}, {outcome}): {detail}.", true);
                    await SendCommandMessageAsync(FileTransfer.Result(push.TransferId, outcome, path, detail));
                }, CancellationToken.None);
                return;
            }
            if (!FileTransfer.TryParsePull(root, _serverUrl, out FileTransfer.PullRequest pull, out string pullError))
            {
                POpsHelpers.Log("FILES", $"Dosya alma reddedildi ({transferId}): {pullError}.", true);
                await SendCommandMessageAsync(FileTransfer.Result(transferId, "rejected", detail: pullError));
                return;
            }
            FileTransferTask = Task.Run(async () =>
            {
                var (outcome, path, detail, size) = await FileTransfer.PullAsync(pull, _hwId, token);
                if (outcome == "done")
                {
                    LocalAudit.Write(LocalAudit.FilePulled(pull.TransferId, path, size, pull.Reason));
                    POpsHelpers.Log("FILES", $"Yönetici dosyayı aldı: {path} ({size} bayt).");
                    ToTray("FILE_PULLED:" + path);
                }
                else POpsHelpers.Log("FILES", $"Dosya alma tamamlanmadı ({pull.TransferId}, {outcome}): {detail}.", true);
                await SendCommandMessageAsync(FileTransfer.Result(pull.TransferId, outcome, path, detail));
            }, CancellationToken.None);
        }

        // Testler: son başlatılan aktarım
        internal Task FileTransferTask { get; private set; } = Task.CompletedTask;

        // ------------------------------------------------------------------ sınav modu (bkz. ExamMode)
        private readonly HashSet<string> _examStoppedLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private volatile bool _examNetworkChanged;

        // Tepsi bandı: mesaj ve bitiş zamanı
        internal static string ExamTrayMessage(ExamSettings settings) =>
            "EXAM_ON:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
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
        // Testler: emrin doğrulandığı ve sınavdan çıkılan an
        internal Func<DateTimeOffset> ExamClock { get; set; } = () => DateTimeOffset.UtcNow;

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
                await DenyCapabilityAsync("exam", "exam_mode");
                if (ExamMode.IsActive) await EndExamAsync("capability", reply: true);
                return;
            }
            if (!ExamMode.TryParse(root, ExamClock(), out ExamSettings settings, out string error))
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
            LocalAudit.Write(LocalAudit.ExamStarted(settings));
            POpsHelpers.Log("EXAM", $"SINAV MODU AKTİF: {settings.Allow.Count} izinli kayıt, bitiş {(settings.Until == null ? "yok" : DateTimeOffset.FromUnixTimeSeconds(settings.Until.Value).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))}, {settings.BlockApps.Count} engelli uygulama.");
            ToTray(ExamTrayMessage(ExamMode.Load()));
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
            Interlocked.Exchange(ref _examLeftAt, ExamClock().ToUnixTimeSeconds());
            LocalAudit.Write(LocalAudit.ExamEnded(source));
            POpsHelpers.Log("EXAM", $"Sınav modu bitti ({source}).");
            ToTray("EXAM_OFF");
        }

        // exam_state hiçbir zaman bağlantının ilk mesajı değildir: exam_mode emrine yanıt (reply) emri gönderen sunucuya
        // gider; kendiliğinden değişiklik (until, yetenek kapandı) yalnızca server_info'da exam_mode duyuran sunucuya.
        // server_info henüz gelmediyse gönderilmez: bağlantı sonrası bildirim (ReportExamStateOnConnectAsync) o anki
        // durumu zaten taşır.
        private async Task SendExamStateAsync(bool reply)
        {
            if (!reply && Handshake.Supports(ExamMode.Feature) != true) return;
            long left = Interlocked.Read(ref _examLeftAt);
            await SendCommandMessageAsync(ExamMode.StateMessage(left == 0 ? null : left));
        }

        // Her bağlantıda server_info'dan sonra bir kez (sunucu exam_mode duyurduysa): sunucu sınav durumunu bağlantı
        // başında öğrenir (sınavda değilken de).
        internal async Task ReportExamStateOnConnectAsync()
        {
            if (_examStateReported || Handshake.Supports(ExamMode.Feature) != true) return;
            _examStateReported = true;
            await SendExamStateAsync(reply: false);
        }

        // 2 sn'de bir: süre doldu mu, engelli uygulamalar; 2 dk'da bir ve ağ adresi değişince: izin listesi çözümü
        private async Task ExamLoopAsync(CancellationToken token)
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => _examNetworkChanged = true;
            DateTime lastRefresh = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool refresh = _examNetworkChanged || DateTime.UtcNow - lastRefresh >= ExamMode.RefreshInterval;
                    if (refresh)
                    {
                        lastRefresh = DateTime.UtcNow;
                        _examNetworkChanged = false;
                    }
                    await ExamTickAsync(DateTimeOffset.UtcNow, refresh ? "dönemsel ya da ağ adresi değişti" : null);
                }
                catch (Exception ex) { POpsHelpers.Log("EXAM", $"Sınav modu denetimi başarısız: {ex.Message}", true); }
                try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Bir tur: sınav yeteneği yerel olarak kapalıysa (EXAM_ENABLED=0 ile yeniden kurulum) ya da süre dolduysa
        // (sunucuya ulaşılamasa da) biter; yoksa engelli uygulamalar kapatılır, refreshReason verilmişse izin listesi
        // yeniden çözülür. Dönen: sınav bu turda bitti mi.
        internal async Task<bool> ExamTickAsync(DateTimeOffset now, string refreshReason)
        {
            ExamSettings settings = ExamMode.IsActive ? ExamMode.Load() : null;
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
                    catch (InvalidOperationException) { return (Pid: p.Id, Name: (string)null, Session: 0); }
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
                            LocalAudit.Write(LocalAudit.ExamAppStopped(app, pid));
                            POpsHelpers.Log("EXAM", $"Sınav modunda {app} kapatıldı (PID {pid}).");
                        }
                        ToTray("EXAM_APP_BLOCKED:" + app);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                }
            }
            finally
            {
                foreach (Process p in all) p.Dispose();
            }
        }

        // 4409: sunucu bu kimliği başka bir bilgisayarda bağlı buldu (kopyalanmış kurulum, asıl cihaz bağlı).
        // Olay Günlüğüne çalışma başına bir kez yazılır; log her kopuşta.
        internal void OnCloneRejected(TimeSpan wait)
        {
            if (!_cloneRejectedAudited) LocalAudit.Write(LocalAudit.CloneRejected("command"));
            _cloneRejectedAudited = true;
            POpsHelpers.Log("AGENT", $"[GÜVENLİK] Sunucu bu cihaz kimliğinin ({_hwId}) başka bir bilgisayarda bağlı olduğunu bildirdi (4409): "
                + $"bu kurulum kopyalanmış olabilir (bkz. POpsAgent.exe --generalize). {wait.TotalMinutes:0.0} dk sonra yeniden denenecek.", true);
        }

        // Komut tüneli kuruldu: DNS politika izleme başlar (yalnızca ilk bağlantıda; sonra açık kalır). Politika ve
        // dns_domains her dakika PolicyPollingLoop'ta yenilenir.
        // Yeni komut bağlantısı: server_info yeniden beklenir, onaysız sonuçlar bu bağlantıda yeniden gönderilir
        internal void OnCommandSocketOpened()
        {
            UpdateResults.OnConnected();
            Results.OnConnected();
            // update_progress: ilk mesaj heartbeat olana kadar gönderilmez; son aşama yeni bağlantıda bir kez daha gider
            _heartbeatSent = false;
            _forwardedProgress = null;
            // exam_state bu bağlantıda server_info'dan sonra yeniden bildirilir
            _examStateReported = false;
        }

        // Bu bağlantıda ilk heartbeat gitti mi (sunucu cihazı ilk mesajın dna_payload'ından kaydeder)
        private volatile bool _heartbeatSent;
        // Bu bağlantıda iletilen son updater aşaması (aşama|deneme|zaman)
        private string _forwardedProgress;

        internal void OnHeartbeatSent() => _heartbeatSent = true;

        // update_progress: yalnızca sunucu özelliği duyurduysa ve bu bağlantıda heartbeat gittiyse; en iyi çaba (soket
        // kapalıysa düşer, saklanmaz). Dönen: gönderildi mi.
        internal async Task<bool> ReportUpdateProgressAsync(Dictionary<string, object> message)
        {
            if (!_heartbeatSent || Handshake.Supports(AgentUpdate.ProgressFeature) != true) return false;
            return await TrySendCommandMessageAsync(message);
        }

        // Heartbeat döngüsünde: updater'ın yazdığı aşama (installing, waiting_installer) değiştiyse sunucuya iletilir
        internal async Task ForwardUpdateProgressAsync()
        {
            UpdateProgressRecord record = AgentUpdate.PendingProgress();
            if (record == null) return;
            string key = $"{record.Stage}|{record.Attempt}|{record.At}";
            if (key == _forwardedProgress) return;
            if (await ReportUpdateProgressAsync(AgentUpdate.ProgressMessage(record.Stage, record.ToVersion, record.Attempt, record.Of, record.Detail)))
                _forwardedProgress = key;
        }

        internal void OnCommandChannelConnected()
        {
            DnsPolicyMonitor.Configure(_currentPolicy, _hwId, _serverUrl);
            DnsPolicyMonitor.Start();
        }

        // DNS izleme statiktir: otomatik karantina ve DNS hataları yalnızca çalışan Worker'a gider. ExecuteAsync'in
        // başında bağlanır (izleme ancak komut tüneli kurulunca başlar), Dispose'da yalnızca hâlâ bu Worker'ınsa
        // bırakılır. Testte kurulup başlatılmayan Worker da, atılmış Worker da hiçbir şey almaz.
        internal void BindDnsPolicyMonitor() => DnsPolicyMonitor.Bind(_dnsQuarantine, _dnsErrorReporter);

        public override void Dispose()
        {
            DnsPolicyMonitor.Release(_dnsQuarantine, _dnsErrorReporter);
            base.Dispose();
            GC.SuppressFinalize(this);
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

        // Karantina sürerken 5 dakikada bir sunucu adı yeniden çözülür; adres değiştiyse kurallar yenilenir
        private async Task IsolationRefreshLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(NetworkIsolation.RefreshInterval, token); }
                catch (OperationCanceledException) { return; }
                if (NetworkIsolation.IsActive) await NetworkIsolation.RefreshServerAddressesAsync(_serverUrl, "periyodik denetim");
            }
        }

        private async Task PolicyPollingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ApplyPolicy(await FetchPolicyJsonAsync(token));
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

        // Anahtarı olan ajan kendini tanıtır (X-Agent-Id + X-Agent-Secret): sunucu cihazın laboratuvarının DNS ayarını ve
        // modül listesini döner (bkz. AgentModules). Başlıklar yönlendirme izlemeyen istemciyle gider (bkz. AgentHttp).
        // Anahtar yoksa kurum geneli politika başlıksız istenir.
        internal async Task<string> FetchPolicyJsonAsync(CancellationToken token)
        {
            if (AgentHttp.CanReport && POpsHelpers.IsSecureServerUrl(_serverUrl))
            {
                var (status, body) = await AgentHttp.SendAsync(HttpMethod.Get, _serverUrl, "/api/agent_policies", _hwId, null, "Politika");
                if (status >= 200 && status < 300 && body != null) return body;
                throw new HttpRequestException(status == null ? "yanıt yok" : $"HTTP {status}");
            }
            return await _httpClient.GetStringAsync(_serverUrl.TrimEnd('/') + "/api/agent_policies", token);
        }

        private static readonly JsonSerializerOptions PolicyJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        internal void ApplyPolicy(string json)
        {
            var policy = JsonSerializer.Deserialize<AgentPolicy>(json, PolicyJson);
            if (policy == null) return;
            using (JsonDocument doc = JsonDocument.Parse(json)) OnModulesChanged(AgentModules.Apply(doc.RootElement));
            _currentPolicy = policy;
            DnsPolicyMonitor.Configure(policy, _hwId, _serverUrl);
            _health.PolicySynced();

            if (!string.IsNullOrWhiteSpace(policy.FairUseText) && !_fairUseAcknowledged)
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(policy.FairUseText));
                _trayPipe?.SendCommandToDesktop($"SHOW_FAIR_USE:{b64}");
            }
        }

        // Modül açıldı/kapandı: bir kez loglanır ve Olay Günlüğüne yazılır (servis açılışındaki ilk yanıtta yalnızca
        // kapalı modül varsa). Kapanan Vision oturumu kesilir, tepsinin yardım masası menüsü eşitlenir, yeniden açılan
        // yazılım envanterinin son gönderimi unutulur (kapalıyken sunucu listeyi saklamadı).
        internal void OnModulesChanged(ModuleChange change)
        {
            if (change == null || !change.Any) return;
            string closed = change.Closed.Count > 0 ? string.Join(", ", change.Closed) : "-";
            string opened = change.Opened.Count > 0 ? string.Join(", ", change.Opened) : "-";
            POpsHelpers.Log("AGENT", change.First
                ? $"Sunucu bu bilgisayarın laboratuvarında şu modülleri kapattı: {closed}."
                : $"Sunucu modülleri değişti: kapatılan {closed}; açılan {opened}.");
            LocalAudit.Write(LocalAudit.ModulesChanged(change.Closed, change.Opened));

            if (change.Closed.Contains(AgentModules.Vision) && (_isVisionStreamActive || _visionWs != null))
            {
                _visionSessionApproved = false;
                _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
                _ = DisconnectVisionTunnelAsync();
            }
            if (change.Closed.Contains(AgentModules.Helpdesk) || change.Opened.Contains(AgentModules.Helpdesk)) SyncTrayModules();
            if (change.Opened.Contains(AgentModules.Software)) _software?.ForgetLastReport();
        }

        // Tepside "Sorun bildir" ve "Taleplerim" yalnızca yardım masası modülü açıkken görünür
        internal static string HelpdeskMenuMessage() => "HELPDESK_MENU:" + (AgentModules.IsEnabled(AgentModules.Helpdesk) ? "1" : "0");

        private void SyncTrayModules() => _trayPipe?.SendCommandToDesktop(HelpdeskMenuMessage());

        // Boru dinlemeye geçince sağlık kontrolü işaretlenir (bkz. AgentStartupHealth). Bağlantı döngüsü bunu
        // beklemez: boru adı başka bir süreçte kalırsa (ör. yerel bir kullanıcı adı önceden aldıysa) tepsi çalışmaz
        // ama ajan sunucuya yine bağlanır; health.json yazılmadığı için güncelleme de başarılı sayılmaz.
        // Servis boyunca tek boru: zaten açıksa bir şey yapılmaz. Dinleme döngüsü hata ve tepsi kopmalarında kendini yeniler.
        internal void EnsureTrayPipeServer()
        {
            if (_trayPipe != null) return;
            _trayPipe = new TrayPipeServer();

            _trayPipe.OnMessageReceived += (message) =>
            {
                if (message.StartsWith("USER_COMMAND:", StringComparison.Ordinal))
                {
                    // Eski tepsi sürümlerinin "WatchDog'u / ekran izlemeyi duraklat" komutları artık kabul edilmez
                    POpsHelpers.Log("AGENT", $"Yok sayılan kullanıcı komutu: {message}");
                }
                else if (message == "FAIR_USE_ACK")
                {
                    _fairUseAcknowledged = true;
                    POpsHelpers.Log("AGENT", "Kullanıcı aydınlatma metnini onayladı.");
                }
                else if (message.StartsWith("ACTIVE_APP:", StringComparison.Ordinal))
                {
                    // Yalnızca süreç adı; heartbeat'te active_window olarak gider
                    string app = ActiveApp.Sanitize(message.Substring("ACTIVE_APP:".Length));
                    if (app != null) _activeApp = app;
                }
                else if (message.StartsWith("ACTIVE_WINDOW:", StringComparison.Ordinal))
                {
                    // Eski tepsi pencere başlığı gönderir: KVKK gereği sunucuya iletilmez (bkz. ActiveApp)
                }
                else if (message.StartsWith(VisionSessionStart.TunnelPrefix, StringComparison.Ordinal))
                {
                    _ = Task.Run(() => StartVisionFromTrayAsync(message));
                }
                else if (message.StartsWith("REJECT_VISION_TUNNEL:", StringComparison.Ordinal))
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
                else if (message.StartsWith("UNLOCK_BYPASS:", StringComparison.Ordinal))
                {
                    _ = HandleBypassAttemptAsync(message.Substring("UNLOCK_BYPASS:".Length));
                }
                else if (message.StartsWith("VISION_MONITORS:", StringComparison.Ordinal))
                {
                    if (_isVisionStreamActive && BinaryVision) _ = _visionRelay.ForwardMonitorsAsync(message.Substring("VISION_MONITORS:".Length));
                }
                else if (message.StartsWith("CLIPBOARD:", StringComparison.Ordinal))
                {
                    _ = _visionRelay.ForwardClipboardAsync(message.Substring("CLIPBOARD:".Length), ClipboardAllowed);
                }
                else if (message.StartsWith("TICKET_CREATE:", StringComparison.Ordinal))
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
                else if (message.StartsWith(UserMessages.AckPrefix, StringComparison.Ordinal) || message.StartsWith(PowerActions.LockResultPrefix, StringComparison.Ordinal))
                {
                    _ = OnTrayReplyAsync(message);
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

            // Vision v2 karesi (işaretli): yalnızca yayın açıkken ve sunucu ikili kareyi destekliyorsa
            _trayPipe.OnVisionFrame += data =>
            {
                if (_isVisionStreamActive && BinaryVision) _ = _visionRelay.ForwardFrameAsync(data);
            };

            _trayPipe.OnDisconnected += () =>
            {
                _visionSessionApproved = false;
                _activeApp = null;
            };

            // Kilit ekranı tepsiyle birlikte kapanmış olabilir: karantina sürüyorsa yeniden gösterilir
            _trayPipe.OnConnected += () =>
            {
                _quarantine.SyncTray();
                if (ExamMode.IsActive) ToTray(ExamTrayMessage(ExamMode.Load()));
                SyncTrayModules();
                string configError = ConfigErrorMessage();
                if (configError != null) _trayPipe?.SendCommandToDesktop(configError);
                // Süren geri sayım ve okundu onayı bekleyen mesajlar yeniden gösterilir
                Power.SyncTray();
                Messages.SyncTray();
            };

            _trayPipe.Start().ContinueWith(_ => _startupHealth.Mark(StartupCheck.Pipe), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
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

        // Testler: Vision tünelinin açılması (gerçek sunucuya bağlanılmaz; true: tünel açıldı), Vision olaylarının Olay
        // Günlüğü kaydı ve sunucunun olay günlüğüne giden kayıt (POST /api/logs)
        internal Func<Task<bool>> VisionTunnelOverride { get; set; }
        internal Action<LocalAuditEvent> AuditOverride { get; set; }
        internal Func<AgentLogPayload, Task> DeviceLogOverride { get; set; }

        private void Audit(LocalAuditEvent item)
        {
            if (AuditOverride != null) AuditOverride(item);
            else LocalAudit.Write(item);
        }

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
                Audit(LocalAudit.VisionStarted(_visionSessionId, _visionRequestedBy, _visionUserApproved));
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
            Audit(LocalAudit.VisionStartedWhileLocked(_visionSessionId, _visionRequestedBy, mandatory));
            _visionNoticeArmed = true;
            ToTray(VisionSessionNotice.OnMessage(new VisionNoticeInfo(_visionSessionId, _visionRequestedBy, _visionReason, mandatory)));
            AgentLogPayload log = VisionLockedStartLog(_visionSessionId, _visionRequestedBy, mandatory);
            _ = DeviceLogOverride != null
                ? DeviceLogOverride(log)
                : AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/logs/", _hwId), _hwId, log, "Kilitliyken başlayan Vision oturumu kaydı");
        }

        // Sunucuda sıradan bir olay günlüğü kaydı (agent_logs_v2); sunucu bu türe özel bir şey yapmaz
        internal static AgentLogPayload VisionLockedStartLog(string sessionId, string requestedBy, bool mandatory) => new AgentLogPayload
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
                await DenyCapabilityAsync("vision", "vision_tunnel");
                return;
            }
            if (!AgentModules.IsEnabled(AgentModules.Vision))
            {
                await DenyCapabilityAsync("vision", "vision_tunnel", reason: AgentModules.DisabledReason);
                return;
            }
            if (VisionTunnelOverride != null)
            {
                _isVisionStreamActive = await VisionTunnelOverride();
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
            VisionAuthSelection auth = VisionChannel.SelectHeaders(secret, AgentCredentials.GetEnrollToken(), AppVersion);
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
                Audit(LocalAudit.VisionFinished(_visionSessionId, _visionRequestedBy, _visionUserApproved));
                _visionAuditActive = false;
            }
            // Oturum bitti: kilitliyken başlayan oturumun bildirimi kapanır (tepsi STOP_CAPTURE'da da kapatır; bu yol
            // sunucunun tüneli kapattığı durumu da kapsar)
            if (_visionNoticeArmed)
            {
                _visionNoticeArmed = false;
                ToTray(VisionSessionNotice.Off);
            }
        }

        // Mesaj boyu sınırları (parçalı mesajlar EndOfMessage'a kadar birleştirilir; bkz. WebSocketMessages)
        private const int MaxCommandMessageBytes = 8 * 1024 * 1024;
        private const int MaxVisionMessageBytes = 1024 * 1024;

        // Uzaktan fare/klavye olayı (input_type taşıyan remote_input). Ekran önizlemesi ve FPS ayarı girdi değildir.
        private static bool IsInputEvent(JsonElement root) => root.TryGetProperty("input_type", out _);

        // Reddedilirse capability_denied'ın yeteneği ve nedeni; yerel yetenek kilidi önce, sonra sunucu modülü, sonra onay
        private (string Capability, string Reason) VisionDenial(bool isInputEvent)
        {
            if (AgentCapabilities.VisionEnabled && !AgentModules.IsEnabled(AgentModules.Vision)) return ("vision", AgentModules.DisabledReason);
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
                        string targetDevice = root.GetProperty("device").GetString();
                        if (targetDevice != _hwId) continue;
                        var (denial, denialReason) = VisionDenial(IsInputEvent(root));
                        if (denial != null) { await DenyCapabilityAsync(denial, "remote_input", reason: denialReason); continue; }
                        _trayPipe?.SendCommandToDesktop(message);
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
        internal bool BinaryVision => Handshake.Supports(VisionRelay.BinaryFeature) == true;

        // Pano yalnızca kullanıcının kabul ettiği (zorunlu bildirimli değil), açık bir v2 oturumunda
        private bool ClipboardAllowed => _isVisionStreamActive && _visionSessionApproved && _visionUserApproved && BinaryVision;

        // Testler: tepsiye giden mesajlar
        internal Action<string> TrayOverride { get; set; }
        // Testler: tepsi bağlı mı / etkin konsol oturumunda mı (null: gerçek boru)
        internal Func<bool> TrayConnectedOverride { get; set; }
        internal Func<bool> TrayInConsoleOverride { get; set; }

        private void ToTray(string message)
        {
            if (TrayOverride != null) TrayOverride(message);
            else _trayPipe?.SendCommandToDesktop(message);
        }

        private bool TrayConnected() => TrayConnectedOverride != null ? TrayConnectedOverride() : _trayPipe?.IsConnected == true;

        // Kilitleme tepsiden yalnızca tepsi etkin konsol oturumundaysa (RDP oturumundaki tepsi konsolu kilitleyemez)
        private bool TrayInConsoleSession()
        {
            if (TrayInConsoleOverride != null) return TrayInConsoleOverride();
            TrayPipeServer pipe = _trayPipe;
            return pipe != null && pipe.IsConnected && pipe.ClientSession != UserSessionLauncher.NoSession && pipe.ClientSession == SessionTasks.ConsoleSession();
        }

        // Tepsinin güç/mesaj yanıtları: "USER_MESSAGE_ACK:<task_id>" (kullanıcı Tamam'a bastı),
        // "POWER_LOCK_RESULT:<task_id>:1|0" (LockWorkStation). Dönen: bekleyen bir göreve aitti.
        internal async Task<bool> OnTrayReplyAsync(string message)
        {
            try
            {
                if (message.StartsWith(UserMessages.AckPrefix, StringComparison.Ordinal))
                    return await Messages.OnAcknowledgedAsync(message.Substring(UserMessages.AckPrefix.Length));
                if (message.StartsWith(PowerActions.LockResultPrefix, StringComparison.Ordinal))
                    return Power.OnTrayLockResult(message.Substring(PowerActions.LockResultPrefix.Length));
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Tepsi yanıtı işlenemedi: {ex.Message}", true); }
            return false;
        }

        // Güç işlemi ve kullanıcı mesajı sonucu (execute'un result'ıyla aynı biçim ve aynı saklama/onay yolu)
        private Task SendTaskResultAsync(int taskId, string output, int exitCode) =>
            SendResultAsync(taskId, new { type = "result", pc_name = _hwId, task_id = taskId, output, exit_code = exitCode });

        internal void StartCapture(int fps)
        {
            if (!BinaryVision)
            {
                ToTray($"START_CAPTURE:{fps}");
                return;
            }
            _visionRelay.Reset();
            ToTray($"START_CAPTURE_V2:{fps}");
            if (_visionUserApproved) ToTray("CLIPBOARD_SHARE:1");
        }

        // Görüntüleyiciden (oturumu tutan yönetici): select_monitor, set_quality, clipboard. Onaylı oturum gerekir.
        internal async Task HandleVisionControlAsync(JsonElement root)
        {
            if (root.TryGetProperty("device", out JsonElement device) && device.ValueKind == JsonValueKind.String && device.GetString() != _hwId) return;
            string action = root.TryGetProperty("action", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : "vision_control";
            var (denial, denialReason) = VisionDenial(isInputEvent: true);
            if (denial != null)
            {
                await DenyCapabilityAsync(denial, action, reason: denialReason);
                return;
            }
            string trayMessage = VisionRelay.TrayMessageFor(root, ClipboardAllowed, out LocalAuditEvent audit);
            if (trayMessage == null)
            {
                POpsHelpers.Log("VISION", $"Görüntüleyici mesajı uygulanmadı ({LogText.Safe(action, 40)}): geçersiz, çok büyük ya da pano için kabul edilmiş oturum yok.");
                return;
            }
            if (audit != null) Audit(audit);
            ToTray(trayMessage);
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

        private async Task ReceiveCommandsAsync(ClientWebSocket ws, CancellationToken stoppingToken)
        {
            var buffer = new byte[16384];
            while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var (message, messageType) = await WebSocketMessages.ReceiveTextAsync(ws, buffer, MaxCommandMessageBytes, stoppingToken);
                    if (messageType == WebSocketMessageType.Close) break;
                    await HandleServerMessageAsync(message, ws, stoppingToken);
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

        // Sunucudan gelen tek komut mesajı (ReceiveCommandsAsync; testlerde doğrudan çağrılır). Çözümlenemeyen JSON
        // çağırana gider. Tanınmayan action yok sayılır (bkz. CommandDispatcher).
        internal Task HandleServerMessageAsync(string message, ClientWebSocket ws, CancellationToken stoppingToken) =>
            Dispatcher.DispatchAsync(message, ws, stoppingToken);

        // Sunucu komutlarının tablosu (eylem -> işleyici)
        internal CommandDispatcher Dispatcher { get; }

        // İşleyiciler burada elle bağlanır. Henüz Worker'da duran gruplar mevcut metotlarını saran DelegateHandler'dır;
        // sonraki bölme adımlarında kendi sınıflarına taşınırlar (bkz. docs/design/worker-split.md).
        private CommandDispatcher BuildDispatcher() => new CommandDispatcher(new ICommandHandler[]
        {
            new HandshakeHandler(Handshake, ReportExamStateOnConnectAsync),
            new ResultAckHandler(_outbox),
            new UpdateHandler(_httpClient, _serverUrl, ReportUpdateProgressAsync),
            new WakeOnLanHandler(_gate),
            new PatchesHandler(_patches, _gate),
            new IdentityHandler(UpdateIdentityFile),
            new SecretsHandler(() => _hwId, () => Binding, () => _software, () => _commandUsesDeviceSecret, TrySendCommandMessageAsync, LocalAudit.Write),
            new InventoryHandler(() => _cachedInventory, _serverUrl, () => _hwId, _health),
            new QuarantineHandler(() => _quarantine, _serverUrl, () => _hwId),
            new ExecuteHandler(() => _commandRunner, _gate, _outbox, () => _hwId, LocalAudit.Write, Power, Messages),
            new WingetInstallHandler(() => _commandRunner, _gate, _outbox, () => _hwId, LocalAudit.Write),
            // a3: CapabilitiesHandler, VisionHandler (RemoteInputHandler aşağıda)
            new DelegateHandler(command => HandleSetCapabilitiesAsync(command.Root), "set_capabilities"),
            new DelegateHandler(HandleVisionCommandAsync, "start_stream", "start_vision_session", "stop_stream"),
            // a4: ExamHandler, FileTransferHandler; uzaktan güç işlemi ve kullanıcıya mesaj (yalnızca X-Agent-Features'ta
            // duyurulduğu için gelir)
            new DelegateHandler(command => HandleExamModeAsync(command.Root), "exam_mode"),
            new DelegateHandler(command => HandleFileTransferAsync(command.Action, command.Root, command.Stopping), "file_push", "file_pull"),
            new DelegateHandler(command => Power.HandleAsync(command.Root, command.Stopping), "power"),
            new DelegateHandler(command => Messages.HandleAsync(command.Root, command.Stopping), "user_message"),
        }, remoteInput: new DelegateHandler(HandleRemoteInputAsync, CommandDispatcher.RemoteInput));

        // "type": "remote_input": ekran önizlemesi (get_thumbnail; yanıt geldiği soketten gider) ya da tepsiye giden
        // uzaktan fare/klavye
        private async Task HandleRemoteInputAsync(ServerCommand command)
        {
            string message = command.Raw;
            JsonElement root = command.Root;
            ClientWebSocket ws = command.Connection;
            CancellationToken stoppingToken = command.Stopping;

            string targetDevice = root.TryGetProperty("device", out var devProp) ? devProp.GetString() : "";
            if (targetDevice != _hwId) return;

            string act = root.TryGetProperty("action", out var actProp) ? actProp.GetString() : "";
            // Ekran önizlemesi ve uzaktan fare/klavye Vision yeteneğidir
            var (denial, denialReason) = VisionDenial(IsInputEvent(root));
            if (denial != null)
            {
                await DenyCapabilityAsync(denial, string.IsNullOrEmpty(act) ? "remote_input" : act, reason: denialReason);
                return;
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
                }, stoppingToken);
            }
            else
            {
                _trayPipe?.SendCommandToDesktop(message);
            }
        }

        // start_stream (eski), start_vision_session, stop_stream: önce Vision yeteneği, sonra Vision modülü
        private async Task HandleVisionCommandAsync(ServerCommand command)
        {
            string message = command.Raw;
            JsonElement root = command.Root;
            string action = command.Action;
            CancellationToken stoppingToken = command.Stopping;
            if ((action == "start_stream" || action == "start_vision_session") && !AgentCapabilities.VisionEnabled)
            {
                await DenyCapabilityAsync("vision", action);
            }
            else if ((action == "start_stream" || action == "start_vision_session") && !AgentModules.IsEnabled(AgentModules.Vision))
            {
                await DenyCapabilityAsync("vision", action, reason: AgentModules.DisabledReason);
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
                        Audit(LocalAudit.VisionStarted(_visionSessionId, _visionRequestedBy, false));
                        _visionAuditActive = true;
                    }
                    StartCapture(fps);
                }
            }
            else if (action == "stop_stream") { 
                _trayPipe?.SendCommandToDesktop("STOP_CAPTURE"); 
                await DisconnectVisionTunnelAsync(); 
            }
            else if (action == "start_vision_session")
            {
                _visionSessionId = root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
                _visionRequestedBy = root.TryGetProperty("requested_by", out var requester) && requester.ValueKind == JsonValueKind.String
                    ? requester.GetString()
                    : root.TryGetProperty("admin_name", out var admin) && admin.ValueKind == JsonValueKind.String ? admin.GetString() : null;
                _pendingVisionMandatory = root.TryGetProperty("is_mandatory", out var mandatory) && mandatory.ValueKind == JsonValueKind.True;
                _visionReason = root.TryGetProperty("reason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String ? reasonProp.GetString() : null;
                // Onay ya da geri sayım tepsidedir; oturum yalnızca tepsinin START_VISION_TUNNEL'ı ile başlar
                ToTray(message);
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
        // yoksa panel ajanın gerçek durumunu gösterir. Donanım bilgisi okunamadıysa dna_payload boş nesnedir (null
        // değil; şema nesne ister, sunucu ikisini de boş sayar).
        internal object HeartbeatPayload() => new
        {
            hw_id = _hwId,
            hostname = _pcName,
            lab_name = "Atanmamis_Cihazlar",
            status = "Online",
            active_window = _activeApp ?? "-",
            quarantined = _quarantine.IsLocked,
            dna_payload = _cachedDna ?? new object(),
            agent_health = _health.Snapshot(
                _trayPipe?.IsConnected == true,
                AgentCapabilities.VisionEnabled,
                _visionWs?.State == WebSocketState.Open,
                _quarantine.ScreenLocked,
                _quarantine.NetworkIsolated,
                _quarantine.LastIsolationError)
        };

        private async Task SendHeartbeatAsync(CancellationToken token)
        {
            string json = JsonSerializer.Serialize(HeartbeatPayload());
            var bytes = Encoding.UTF8.GetBytes(json);

            await _wsCommandLock.WaitAsync(token);
            try
            {
                if (_commandWs != null && _commandWs.State == WebSocketState.Open)
                {
                    await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                    OnHeartbeatSent();
                }
            }
            finally { _wsCommandLock.Release(); }
        }

        // Sunucunun "set_capabilities" isteği: yalnızca kapatma uygulanır (bkz. AgentCapabilities). Vision kapandıysa
        // süren yayın hemen durdurulur. Son durum sunucuya "capabilities" olarak bildirilir. Sınav yeteneği kapandıysa
        // süren sınav biter (exam_state ile).
        private async Task HandleSetCapabilitiesAsync(JsonElement request)
        {
            var changed = AgentCapabilities.ApplyServerRequest(request);
            foreach (string capability in changed.Disabled)
                LocalAudit.Write(LocalAudit.CapabilityChanged(capability.Replace("_enabled", "", StringComparison.Ordinal), true, false));
            // Eş önbelleği kapandıysa önbellek silinir, sunum durur
            if (changed.Disabled.Contains(AgentCapabilities.PeerCacheKey)) await PeerCache.SyncAsync();
            if (!AgentCapabilities.VisionEnabled && _isVisionStreamActive)
            {
                _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
                await DisconnectVisionTunnelAsync();
            }
            // Güç işlemleri kapandıysa süren geri sayım da durur
            if (!AgentCapabilities.PowerEnabled && Power.CancelForDisabledCapability())
                POpsHelpers.Log("AGENT", "Güç işlemleri kapatıldı; süren geri sayım durduruldu.");
            await SendCommandMessageAsync(AgentCapabilities.StatusMessage());
            if (!AgentCapabilities.ExamEnabled && ExamMode.IsActive) await EndExamAsync("capability");
        }

        // capability_denied (bkz. CapabilityGate; dakikada bir sınırı tüm eylemlerde ortak)
        private Task DenyCapabilityAsync(string capability, string action, int? taskId = null, string reason = null, string transferId = null) =>
            _gate.DenyAsync(capability, action, taskId, reason, transferId);

        // Testler içindir: giden komut mesajları sokete yazılmaz, buraya verilir (dönen: gönderildi mi). Uzaktan komutun
        // çalıştırıcısı ve karantina denetimi de (gerçek güvenlik duvarına dokunmayan) sahteleriyle değiştirilebilir.
        internal Func<object, Task<bool>> SendOverride { get; set; }
        internal CommandRunner CommandRunner { get => _commandRunner; set => _commandRunner = value; }
        internal QuarantineControl Quarantine { get => _quarantine; set => _quarantine = value; }
        internal string HwId { get => _hwId; set => _hwId = value; }
        internal TrayPipeServer TrayPipe => _trayPipe;
        internal SoftwareReporter Software { get => _software; set => _software = value; }

        private async Task SendCommandMessageAsync(object payload) => await TrySendCommandMessageAsync(payload);

        // Dönen: mesaj o anki komut soketine yazıldı mı
        private async Task<bool> TrySendCommandMessageAsync(object payload)
        {
            if (SendOverride != null) return await SendOverride(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await _wsCommandLock.WaitAsync();
            try
            {
                if (_commandWs == null || _commandWs.State != WebSocketState.Open) return false;
                await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"Sunucuya mesaj gönderilemedi: {ex.Message}", true);
                return false;
            }
            finally { _wsCommandLock.Release(); }
        }

        // Görev sonucu ve heartbeat'te bekleyen sonuçlar (bkz. ResultOutbox)
        private Task SendResultAsync(int taskId, object result) => _outbox.SendResultAsync(taskId, result);
        internal Task FlushPendingResultsAsync() => _outbox.FlushPendingResultsAsync();

        // POpsUpdater'ın bıraktığı sonuç (update-result.json) sunucuya "update_result" olarak iletilir. Updater sonucu
        // yeni sürüm açıldıktan sonra yazdığı için her heartbeat'te bakılır. Onaylı sunucuda dosya onaya kadar kalır.
        internal async Task ReportUpdateResultAsync(CancellationToken token)
        {
            Dictionary<string, object> message = AgentUpdate.PendingResultMessage();
            if (message == null) return;
            // Yerel denetim izi (1030) sunucu bağlantısından bağımsız, sonuç ilk görüldüğünde
            LocalAudit.Write(AgentUpdate.PendingResultAudit(message));

            string resultId = message["result_id"] as string;
            UpdateResultReporter.Step step = UpdateResults.Next(resultId);
            if (step == UpdateResultReporter.Step.Nothing || step == UpdateResultReporter.Step.Wait) return;

            if (!await TrySendCommandMessageAsync(message)) return;

            if (step == UpdateResultReporter.Step.SendAndMarkReported)
            {
                AgentUpdate.MarkResultReported();
                POpsHelpers.Log("UPDATE", $"Güncelleme sonucu sunucuya iletildi: {message["status"]}.");
            }
            else
            {
                UpdateResults.Sent(resultId);
                POpsHelpers.Log("UPDATE", $"Güncelleme sonucu sunucuya iletildi, onay bekleniyor: {message["status"]} ({resultId}).");
            }
        }

        private string InitializeIdentity()
        {
            try
            {
                string dir = Path.GetDirectoryName(_identityFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // Eski sürümler bu klasörü Everyone:FullControl ile açıyordu; oturum açan herkes kimlik
                // dosyasını değiştirip başka bir cihaz gibi bağlanabiliyordu. ACL her başlangıçta yeniden kurulur.
                HardwareInfo.SecureDataDirectory(dir);
                // Watchdog'u duraklatma özelliği kaldırıldı; eski sürümden kalan bayrak temizlenir
                try { File.Delete(Path.Combine(dir, "watchdog_pause.flag")); } catch { }

                if (File.Exists(_identityFilePath))
                {
                    string savedId = File.ReadAllText(_identityFilePath).Trim();
                    if (!string.IsNullOrEmpty(savedId) && savedId.StartsWith("HW-", StringComparison.Ordinal)) return savedId;
                }

                string newId = HardwareInfo.GenerateFallbackHash();
                File.WriteAllText(_identityFilePath, newId);
                return newId;
            }
            catch
            {
                return HardwareInfo.GenerateFallbackHash();
            }
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
                Binding.UpdateHwId(newId);
                POpsHelpers.Log("AGENT", $"Kimlik başarıyla güncellendi: {_hwId}");
            }
            catch { }
        }

        private object BuildInventoryInternal()
        {
            try
            {
                return new
                {
                    hw_id = _hwId,
                    hostname = _pcName,
                    cpu = HardwareInfo.GetWmiValue("Win32_Processor", "Name"),
                    ram = HardwareInfo.GetTotalRam(),
                    motherboard = HardwareInfo.GetWmiValue("Win32_BaseBoard", "Product"),
                    gpu = HardwareInfo.GetWmiValue("Win32_VideoController", "Name"),
                    os_version = HardwareInfo.GetWmiValue("Win32_OperatingSystem", "Caption"),
                    ip_address = HardwareInfo.GetLocalIPAddress(),
                    mac_address = HardwareInfo.GetMacAddress(),
                    disk_info = GetDiskInfo(),
                    dna = _cachedDna
                };
            }
            catch { return null; }
        }

        private static string GetDiskInfo()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var drive in DriveInfo.GetDrives()) if (drive.IsReady && drive.DriveType == DriveType.Fixed) sb.Append(CultureInfo.InvariantCulture, $"{drive.Name} {drive.TotalFreeSpace / (1024L * 1024 * 1024)}GB Boş / {drive.TotalSize / (1024L * 1024 * 1024)}GB Toplam | ");
                return sb.ToString().TrimEnd(' ', '|');
            }
            catch { }
            return "-";
        }

        // Ağ karantinası: bkz. NetworkIsolation (eski uygulama güvenlik duvarında hiçbir kural oluşturamıyordu). Karantinada
        // eş önbelleği sunulmaz; değişiklikten sonra arka planda yeniden denetlenir (bkz. PeerCache).
        private async Task<bool> EnableNetworkIsolationAsync()
        {
            bool applied = await NetworkIsolation.EnableAsync(_serverUrl);
            _ = Task.Run(PeerCache.SyncAsync);
            return applied;
        }

        private async Task<bool> DisableNetworkIsolationAsync()
        {
            bool removed = await NetworkIsolation.DisableAsync();
            _ = Task.Run(PeerCache.SyncAsync);
            return removed;
        }

        // Servis dururken eş önbelleği sunucusu kapanır, güvenlik duvarı kuralı kaldırılır (önbellek dosyası kalır;
        // güncellemeyle açılan yeni sürüm sunmayı sürdürür)
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            await PeerCache.ShutdownAsync();
        }
    }

    public sealed class TrayPipeServer : IDisposable
    {
        private const int MaxPipeMessageBytes = 32 * 1024 * 1024;
        private CancellationTokenSource _cts;
        private NamedPipeServerStream _pipeServer;
        private TaskCompletionSource<bool> _listening;
        public event Action<string> OnMessageReceived = delegate { };
        public event Action<byte[]> OnFrameReceived = delegate { };
        // Vision v2 karesi (POps.Shared.VisionFrame.PipeMagic ile başlar)
        public event Action<byte[]> OnVisionFrame = delegate { };
        // Tepsi bağlantısı koptuğunda (onaylı Vision oturumu da onunla biter)
        public event Action OnDisconnected = delegate { };
        // Doğrulanmış tepsi bağlandı (servis açılışı, tepsinin yeniden başlaması, oturum değişimi)
        public event Action OnConnected = delegate { };
        private readonly object _writeLock = new object();

        // Boruya yalnızca kurulum klasöründeki tepsi bağlanabilir (bkz. PipeClientVerifier)
        private static readonly string TrayExePath = Path.Combine(AppContext.BaseDirectory, "POpsTray.exe");

        // Testler başka bir ad ve sahte istemci denetimi kullanır (gerçek tepsiyle ve kurulu servisle çakışmasın)
        internal static string PipeName { get; set; } = "POpsTrayPipe";
        // null: kurulu tepsi doğrulaması (PipeClientVerifier). Dönen: ret nedeni, null kabul.
        internal static Func<Microsoft.Win32.SafeHandles.SafePipeHandle, string> ClientCheckOverride { get; set; }

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
        // Bağlı tepsinin oturumu (bağlantı yoksa ya da bilinmiyorsa NoSession)
        public uint ClientSession { get; private set; } = UserSessionLauncher.NoSession;

        public bool IsConnected
        {
            get
            {
                try { return _pipeServer?.IsConnected == true; }
                catch (ObjectDisposedException) { return false; }
            }
        }
        public void Stop() { _cts?.Cancel(); _pipeServer?.Dispose(); }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }

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
            string pipeName = PipeName;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ps = new PipeSecurity();
                    // Yalnızca oturum açmış (etkileşimli) kullanıcının tepsi uygulaması bağlanabilir;
                    // ağ ve servis hesapları için Everyone izni kaldırıldı.
                    var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
                    var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    // Servis hesabı (LocalSystem; testlerde testi çalıştıran kullanıcı, bkz. SecureStore.SystemSid)
                    var system = SecureStore.SystemSid;
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
                    uint clientPid = 0;
                    string rejection = ClientCheckOverride != null
                        ? ClientCheckOverride(_pipeServer.SafePipeHandle)
                        : PipeClientVerifier.Verify(_pipeServer.SafePipeHandle, TrayExePath, out clientPid);
                    if (rejection != null)
                    {
                        POpsHelpers.Log("PIPE", $"[GÜVENLİK] Tepsi borusuna doğrulanmamış istemci bağlandı, bağlantı kesildi: {rejection}", true);
                        _pipeServer.Disconnect();
                        // Sürekli bağlanıp gerçek tepsiyi dışarıda bırakmaya çalışan istemciyi yavaşlatır
                        await Task.Delay(2000, token);
                        continue;
                    }
                    ClientSession = clientPid == 0 ? UserSessionLauncher.NoSession : UserSessionLauncher.SessionOf((int)clientPid);
                    ClientUser = UserSessionLauncher.SessionUser(ClientSession);
                    _lastPipeError = null;
                    POpsHelpers.Log("PIPE", "🟢 Tepsi bağlandı (doğrulandı).");
                    try { OnConnected?.Invoke(); } catch (Exception ex) { POpsHelpers.Log("PIPE", $"Bağlantı sonrası eşitleme başarısız: {ex.Message}", true); }

                    byte[] lBuf = new byte[4];
                    while (_pipeServer.IsConnected && !token.IsCancellationRequested)
                    {
                        int lRead = 0;
                        while (lRead < 4)
                        {
                            int r = await _pipeServer.ReadAsync(lBuf.AsMemory(lRead, 4 - lRead), token);
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
                            int r = await _pipeServer.ReadAsync(d.AsMemory(total, dLen - total), token);
                            if (r == 0) break;
                            total += r;
                        }
                        if (total == dLen) 
                        {
                            if (VisionFrame.HasPipeMagic(d))
                            {
                                OnVisionFrame?.Invoke(d);
                            }
                            else if (dLen > 2 && d[0] == 0xFF && d[1] == 0xD8)
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
                    ClientSession = UserSessionLauncher.NoSession;
                    OnDisconnected?.Invoke();
                }
            }
        }
    }
}
