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
        // ExecuteAsync açılışta kurar; bağlantı döngüsü onu bekler (testler döngüyü açılış olmadan çalıştırır)
        private Task _slowInitialization = Task.CompletedTask;

        // 🚀 ARTIK SABİT DEĞİL, HELPERS'TAN OKUNACAK
        private string _serverUrl;

        private ClientWebSocket _commandWs;
        private bool _commandUsesDeviceSecret;
        private readonly SemaphoreSlim _wsCommandLock = new(1, 1);
        private readonly SemaphoreSlim _wsVisionLock = new(1, 1);
        // Vision oturumu: tünel, onay, ekran yakalama, Vision v2 (bkz. VisionSession); "remote_input" ve ekran önizlemesi
        private readonly VisionSession _vision;
        private readonly RemoteInputHandler _remoteInput;

        private TrayPipeServer _trayPipe;


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
        // Sınav modu ve dosya aktarımı (bkz. ExamHandler, FileTransferHandler)
        private readonly ExamHandler _exam;
        private readonly FileTransferHandler _files;

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
            _vision = new VisionSession(() => _hwId, _pcName, _serverUrl, _gate, Handshake, ToTray, () => _trayPipe, Audit, _wsVisionLock);
            _remoteInput = new RemoteInputHandler(_vision, _gate, () => _hwId, () => _trayPipe, _wsCommandLock);
            Power = new PowerActions(SendTaskResultAsync, taskId => DenyCapabilityAsync(PowerActions.Capability, PowerActions.ActionName, taskId),
                ToTray, TrayInConsoleSession);
            Messages = new UserMessages(SendTaskResultAsync, taskId => DenyCapabilityAsync(UserMessages.Capability, UserMessages.ActionName, taskId),
                ToTray, TrayConnected);
            _exam = new ExamHandler(_gate, Handshake, TrySendCommandMessageAsync, ToTray, LocalAudit.Write, _serverUrl, () => ExamClock());
            _files = new FileTransferHandler(_gate, _serverUrl, () => _hwId, TrySendCommandMessageAsync, ToTray, LocalAudit.Write);
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

            await RunCommandConnectionAsync(stoppingToken);
        }

        // Sunucuya komut bağlantısı: bağlanır, heartbeat döngüsünü sürdürür, kopunca geri çekilip yeniden bağlanır (servis
        // durana kadar). Testler sahte sunucuyla doğrudan çalıştırır.
        internal async Task RunCommandConnectionAsync(CancellationToken stoppingToken)
        {
            string baseWsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");

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
            bool visionActive = _vision.StreamActive || _vision.SessionApproved || _vision.HasTunnel;
            _vision.RevokeApproval();
            if (visionActive) _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
            await _vision.DisconnectVisionTunnelAsync();
        }

        // ------------------------------------------------------------------ dosya aktarımı (bkz. FileTransferHandler)
        // Testler: emir doğrudan ve son başlatılan aktarım
        internal Task HandleFileTransferAsync(string action, JsonElement root, CancellationToken token) => _files.HandleFileTransferAsync(action, root, token);
        internal Task FileTransferTask => _files.FileTransferTask;

        // ------------------------------------------------------------------ sınav modu (bkz. ExamHandler)
        private volatile bool _examNetworkChanged;
        // Testler: emrin doğrulandığı ve sınavdan çıkılan an
        internal Func<DateTimeOffset> ExamClock { get; set; } = () => DateTimeOffset.UtcNow;

        // Tepsi bağlantısı, set_capabilities, server_info, sınav döngüsü ve testler buradan çağırır (bkz. ExamHandler)
        internal static string ExamTrayMessage(ExamSettings settings) => ExamHandler.ExamTrayMessage(settings);
        internal Task HandleExamModeAsync(JsonElement root) => _exam.HandleExamModeAsync(root);
        internal Task EndExamAsync(string source, bool reply = false) => _exam.EndExamAsync(source, reply);
        internal Task ReportExamStateOnConnectAsync() => _exam.ReportExamStateOnConnectAsync();
        internal Task<bool> ExamTickAsync(DateTimeOffset now, string refreshReason) => _exam.ExamTickAsync(now, refreshReason);

        // 2 sn'de bir: süre doldu mu, engelli uygulamalar; 2 dk'da bir ve ağ adresi değişince: izin listesi çözümü
        private async Task ExamLoopAsync(CancellationToken token)
        {
            // Süreç geneli olay: Worker atılınca bırakılır (bkz. Dispose)
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnExamNetworkAddressChanged;
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

        private void OnExamNetworkAddressChanged(object sender, EventArgs e) => _examNetworkChanged = true;

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
            _exam.OnCommandSocketOpened();
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
            // Sınav döngüsünün ağ olayı ve sınav giriş/çıkış kilidi (bkz. ExamLoopAsync, ExamHandler)
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnExamNetworkAddressChanged;
            _exam.Dispose();
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

            if (change.Closed.Contains(AgentModules.Vision) && (_vision.StreamActive || _vision.HasTunnel))
            {
                _vision.RevokeApproval();
                _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
                _ = _vision.DisconnectVisionTunnelAsync();
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
                    _vision.RevokeApproval();
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
                    _vision.RevokeApproval();
                    _trayPipe?.SendCommandToDesktop("STOP_CAPTURE");
                    _ = _vision.DisconnectVisionTunnelAsync();
                }
                else if (message.StartsWith("UNLOCK_BYPASS:", StringComparison.Ordinal))
                {
                    _ = HandleBypassAttemptAsync(message.Substring("UNLOCK_BYPASS:".Length));
                }
                else if (message.StartsWith("VISION_MONITORS:", StringComparison.Ordinal))
                {
                    if (_vision.StreamActive && _vision.BinaryVision) _ = _vision.Relay.ForwardMonitorsAsync(message.Substring("VISION_MONITORS:".Length));
                }
                else if (message.StartsWith("CLIPBOARD:", StringComparison.Ordinal))
                {
                    _ = _vision.Relay.ForwardClipboardAsync(message.Substring("CLIPBOARD:".Length), _vision.ClipboardAllowed);
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
        internal Func<Task<bool>> VisionTunnelOverride { get => _vision.TunnelOverride; set => _vision.TunnelOverride = value; }
        internal Action<LocalAuditEvent> AuditOverride { get; set; }
        internal Func<AgentLogPayload, Task> DeviceLogOverride { get => _vision.DeviceLogOverride; set => _vision.DeviceLogOverride = value; }

        private void Audit(LocalAuditEvent item)
        {
            if (AuditOverride != null) AuditOverride(item);
            else LocalAudit.Write(item);
        }

        // Tepsi oturumu başlattı (START_VISION_TUNNEL; bkz. VisionSession)
        internal Task StartVisionFromTrayAsync(string message) => _vision.StartVisionFromTrayAsync(message);

        // Komut mesajının boyu sınırı (parçalı mesajlar EndOfMessage'a kadar birleştirilir; bkz. WebSocketMessages)
        private const int MaxCommandMessageBytes = 8 * 1024 * 1024;

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

        // Ekran yakalamanın başlatılması ve görüntüleyici denetimi (bkz. VisionSession)
        internal void StartCapture(int fps) => _vision.StartCapture(fps);
        internal Task HandleVisionControlAsync(JsonElement root) => _vision.HandleVisionControlAsync(root);

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
            new CapabilitiesHandler(_vision, Power, () => _trayPipe, TrySendCommandMessageAsync, LocalAudit.Write, _exam.EndIfCapabilityOffAsync),
            new VisionHandler(_vision, _gate, () => _trayPipe),
            _exam,
            _files,
            new PowerHandler(Power),
            new UserMessageHandler(Messages),
        }, remoteInput: _remoteInput);

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
                _vision.TunnelOpen,
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
}
