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

        // Komut ve Vision soketlerinin yazma kilitleri (servis boyunca yaşar; bkz. CommandChannel, VisionSession)
        private readonly SemaphoreSlim _wsCommandLock = new(1, 1);
        private readonly SemaphoreSlim _wsVisionLock = new(1, 1);
        // Sunucuya komut bağlantısı, heartbeat ve gönderim (bkz. CommandConnection, CommandChannel)
        private readonly CommandChannel _channel;
        private readonly CommandConnection _connection;
        // Vision oturumu: tünel, onay, ekran yakalama, Vision v2 (bkz. VisionSession); "remote_input" ve ekran önizlemesi
        private readonly VisionSession _vision;
        private readonly RemoteInputHandler _remoteInput;
        // Tepsi borusu ve tepsiden gelen mesajlar (bkz. TrayMessageRouter)
        private readonly TrayMessageRouter _tray;
        // Politika eşitleme ve modül değişikliği (bkz. PolicySync); güncellemenin sunucuya bildirimi (bkz. UpdateReporter)
        private readonly PolicySync _policy;
        private readonly UpdateReporter _updates;

        private object _cachedDna;
        private object _cachedInventory;

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
        // Cihaz anahtarının donanıma bağı (hw.bind) ve kopyalanmış kurulum denetimi (bkz. HardwareBinding)
        internal HardwareBinding Binding { get; set; }
        // Yazılım envanteri (yeni secret alınınca son gönderim unutulur)
        private SoftwareReporter _software;
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

            // Tepsi borusu ve bağlantı aşağıda, en son kurulur; aradakiler onları ancak çalışırken okur
            _channel = new CommandChannel(_wsCommandLock);
            _quarantine = new QuarantineControl(message => _tray.Pipe?.SendCommandToDesktop(message),
                EnableNetworkIsolationAsync, DisableNetworkIsolationAsync, audit: LocalAudit.Write);
            _dnsErrorReporter = message => _health.RecordError("dns", message);
            // DNS eşiğindeki otomatik karantina da kilit ekranı + yalıtım yolundan geçer (bkz. AutoQuarantineAsync)
            _dnsQuarantine = reason => _ = AutoQuarantineAsync(reason);
            _patches = new PatchManager(_serverUrl, () => _hwId);
            // Talebin sahibi konsoldaki değil, isteği yapan tepsinin oturumundaki kullanıcı (hızlı kullanıcı değiştirme, RDP)
            _helpdesk = new Helpdesk(_serverUrl, () => _hwId, () => _tray.Pipe?.ClientUser, message => _tray.Pipe?.SendCommandToDesktop(message));
            _activity = new ActivityHistory(_serverUrl, () => _hwId, message => _tray.Pipe?.SendCommandToDesktop(message));
            UpdateResults = new UpdateResultReporter(Handshake);
            // Önceki çalışmadan onay bekleyen sonuçlar okunur
            Results = new ResultSpool(SecureStore.PathOf(ResultSpool.FileName));
            _outbox = new ResultOutbox(Handshake, Results, _channel.TrySendAsync);
            _gate = new CapabilityGate(_channel.TrySendAsync, AgentModules.IsEnabled, TimeProvider.System);
            _vision = new VisionSession(() => _hwId, _pcName, _serverUrl, _gate, Handshake, ToTray, () => _tray.Pipe, Audit, _wsVisionLock);
            _remoteInput = new RemoteInputHandler(_vision, _gate, () => _hwId, () => _tray.Pipe, _channel);
            Power = new PowerActions(SendTaskResultAsync, taskId => DenyCapabilityAsync(PowerActions.Capability, PowerActions.ActionName, taskId),
                ToTray, TrayInConsoleSession);
            Messages = new UserMessages(SendTaskResultAsync, taskId => DenyCapabilityAsync(UserMessages.Capability, UserMessages.ActionName, taskId),
                ToTray, TrayConnected);
            _exam = new ExamHandler(_gate, Handshake, _channel.TrySendAsync, ToTray, LocalAudit.Write, _serverUrl, () => ExamClock());
            _files = new FileTransferHandler(_gate, _serverUrl, () => _hwId, _channel.TrySendAsync, ToTray, LocalAudit.Write);
            Binding = new HardwareBinding(_identityFilePath,
                () => (HardwareInfo.GetWmiValue("Win32_ComputerSystemProduct", "UUID"), HardwareInfo.GetWmiValue("Win32_BIOS", "SerialNumber")));
            _updates = new UpdateReporter(Handshake, UpdateResults, _channel.TrySendAsync, LocalAudit.Write);
            _policy = new PolicySync(_serverUrl, () => _hwId, _httpClient, _health, _vision, () => _tray.Pipe, () => _software, LocalAudit.Write);
            Dispatcher = BuildDispatcher();
            _tray = new TrayMessageRouter(() => new TrayPipeServer(), _startupHealth, _vision, _remoteInput, _channel, () => _hwId, _policy, _helpdesk, _activity,
                Power, Messages, () => _quarantine, HandleBypassAttemptAsync, ToTray, ConfigErrorMessage);
            _connection = new CommandConnection(_serverUrl, _pcName, () => _hwId, _channel, Dispatcher, _updates, _outbox, _vision, _tray,
                () => _quarantine, () => _cachedDna, _health, _startupHealth, () => _slowInitialization, OnCommandSocketOpened,
                _policy.OnCommandChannelConnected, LocalAudit.Write);
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
                await _connection.RunWithoutServerAsync(stoppingToken);
                return;
            }

            // Start background tasks
            _ = Task.Run(() => _policy.PolicyPollingLoop(stoppingToken), stoppingToken);

            // Sunucuya bildirimler (yalnızca cihaz secret'ı varken; bkz. AgentHttp): yazılım envanteri (açılıştan
            // kısa süre sonra, sonra 6 saatte bir), günlük Windows Update taraması, oturum açma/kapama
            var sessions = new SessionReporter(_serverUrl, () => _hwId, _pcName,
                error => _health.RecordError("session", error));
            sessions.UserChanged += _ =>
            {
                _tray.ForgetForegroundApp();
                // Yeni kullanıcı öncekinin DNS ihlalleriyle karantinaya girmesin
                DnsPolicyMonitor.OnUserChanged();
            };
            _software = new SoftwareReporter(_serverUrl, () => _hwId, _health.InventoryUploaded, error => _health.RecordError("inventory", error));
            _ = Task.Run(() => _software.RunAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => _patches.ScheduleLoopAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => sessions.RunAsync(stoppingToken), stoppingToken);
            _ = Task.Run(() => _helpdesk.PollLoopAsync(stoppingToken, () => _tray.Pipe?.IsConnected == true), stoppingToken);
            // Karantinada sunucunun adresi değişirse izin listesi yenilenir (bkz. NetworkIsolation)
            _ = Task.Run(() => IsolationRefreshLoopAsync(stoppingToken), stoppingToken);

            await _connection.RunAsync(stoppingToken);
        }

        // ------------------------------------------------------------------ sunucu bağlantısı (bkz. CommandConnection)
        // Testler: bağlantı döngüsü (sahte sunucuyla), bağlantı kaybı, 4409 ve heartbeat
        internal Task RunCommandConnectionAsync(CancellationToken stoppingToken) => _connection.RunAsync(stoppingToken);
        internal Task OnCommandConnectionLostAsync() => _connection.OnCommandConnectionLostAsync();
        internal void OnCloneRejected(TimeSpan wait) => _connection.OnCloneRejected(wait);
        internal object HeartbeatPayload() => _connection.HeartbeatPayload();

        // ------------------------------------------------------------------ dosya aktarımı (bkz. FileTransferHandler)
        // Testler: emir doğrudan ve son başlatılan aktarım
        internal Task HandleFileTransferAsync(string action, JsonElement root, CancellationToken token) => _files.HandleFileTransferAsync(action, root, token);
        internal Task FileTransferTask => _files.FileTransferTask;

        // ------------------------------------------------------------------ sınav modu (bkz. ExamHandler)
        private volatile bool _examNetworkChanged;
        // Testler: emrin doğrulandığı ve sınavdan çıkılan an
        internal Func<DateTimeOffset> ExamClock { get; set; } = () => DateTimeOffset.UtcNow;

        // set_capabilities, server_info, sınav döngüsü ve testler buradan çağırır (bkz. ExamHandler)
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

        // Yeni komut bağlantısı: server_info yeniden beklenir, onaysız sonuçlar bu bağlantıda yeniden gönderilir
        internal void OnCommandSocketOpened()
        {
            UpdateResults.OnConnected();
            Results.OnConnected();
            // update_progress: ilk mesaj heartbeat olana kadar gönderilmez; son aşama yeni bağlantıda bir kez daha gider
            _updates.OnCommandSocketOpened();
            // exam_state bu bağlantıda server_info'dan sonra yeniden bildirilir
            _exam.OnCommandSocketOpened();
        }

        // ------------------------------------------------------------------ güncelleme bildirimi (bkz. UpdateReporter)
        // Testler: heartbeat gitti, update_progress ve update_result
        internal void OnHeartbeatSent() => _updates.OnHeartbeatSent();
        internal Task<bool> ReportUpdateProgressAsync(Dictionary<string, object> message) => _updates.ReportUpdateProgressAsync(message);
        internal Task ForwardUpdateProgressAsync() => _updates.ForwardUpdateProgressAsync();
        internal Task ReportUpdateResultAsync(CancellationToken token) => _updates.ReportUpdateResultAsync(token);

        // ------------------------------------------------------------------ politika (bkz. PolicySync)
        // Testler: DNS izlemenin başlaması, politika isteği ve uygulanması, tepsinin yardım masası menüsü
        internal void OnCommandChannelConnected() => _policy.OnCommandChannelConnected();
        internal Task<string> FetchPolicyJsonAsync(CancellationToken token) => _policy.FetchPolicyJsonAsync(token);
        internal void ApplyPolicy(string json) => _policy.ApplyPolicy(json);
        internal static string HelpdeskMenuMessage() => PolicySync.HelpdeskMenuMessage();

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

        // ------------------------------------------------------------------ tepsi borusu (bkz. TrayMessageRouter)
        // Testler: borunun açılması, tepsinin güç/mesaj yanıtları
        internal void EnsureTrayPipeServer() => _tray.EnsureTrayPipeServer();
        internal Task<bool> OnTrayReplyAsync(string message) => _tray.OnTrayReplyAsync(message);

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

        // Testler: tepsiye giden mesajlar
        internal Action<string> TrayOverride { get; set; }
        // Testler: tepsi bağlı mı / etkin konsol oturumunda mı (null: gerçek boru)
        internal Func<bool> TrayConnectedOverride { get; set; }
        internal Func<bool> TrayInConsoleOverride { get; set; }

        private void ToTray(string message)
        {
            if (TrayOverride != null) TrayOverride(message);
            else _tray.Pipe?.SendCommandToDesktop(message);
        }

        private bool TrayConnected() => TrayConnectedOverride != null ? TrayConnectedOverride() : _tray.Pipe?.IsConnected == true;

        // Kilitleme tepsiden yalnızca tepsi etkin konsol oturumundaysa (RDP oturumundaki tepsi konsolu kilitleyemez)
        private bool TrayInConsoleSession()
        {
            if (TrayInConsoleOverride != null) return TrayInConsoleOverride();
            TrayPipeServer pipe = _tray.Pipe;
            return pipe != null && pipe.IsConnected && pipe.ClientSession != UserSessionLauncher.NoSession && pipe.ClientSession == SessionTasks.ConsoleSession();
        }

        // Güç işlemi ve kullanıcı mesajı sonucu (execute'un result'ıyla aynı biçim ve aynı saklama/onay yolu)
        private Task SendTaskResultAsync(int taskId, string output, int exitCode) =>
            SendResultAsync(taskId, new { type = "result", pc_name = _hwId, task_id = taskId, output, exit_code = exitCode });

        // Ekran yakalamanın başlatılması ve görüntüleyici denetimi (bkz. VisionSession)
        internal void StartCapture(int fps) => _vision.StartCapture(fps);
        internal Task HandleVisionControlAsync(JsonElement root) => _vision.HandleVisionControlAsync(root);

        // Sunucudan gelen tek komut mesajı (CommandConnection'ın alma döngüsü; testlerde doğrudan çağrılır). Çözümlenemeyen JSON
        // çağırana gider. Tanınmayan action yok sayılır (bkz. CommandDispatcher).
        internal Task HandleServerMessageAsync(string message, ClientWebSocket ws, CancellationToken stoppingToken) =>
            Dispatcher.DispatchAsync(message, ws, stoppingToken);

        // Sunucu komutlarının tablosu (eylem -> işleyici)
        internal CommandDispatcher Dispatcher { get; }

        // İşleyiciler burada elle bağlanır (bkz. docs/design/worker-split.md).
        private CommandDispatcher BuildDispatcher() => new CommandDispatcher(new ICommandHandler[]
        {
            new HandshakeHandler(Handshake, ReportExamStateOnConnectAsync),
            new ResultAckHandler(_outbox),
            new UpdateHandler(_httpClient, _serverUrl, _updates.ReportUpdateProgressAsync),
            new WakeOnLanHandler(_gate),
            new PatchesHandler(_patches, _gate),
            new IdentityHandler(UpdateIdentityFile),
            new SecretsHandler(() => _hwId, () => Binding, () => _software, () => _connection.UsesDeviceSecret, _channel.TrySendAsync, LocalAudit.Write),
            new InventoryHandler(() => _cachedInventory, _serverUrl, () => _hwId, _health),
            new QuarantineHandler(() => _quarantine, _serverUrl, () => _hwId),
            new ExecuteHandler(() => _commandRunner, _gate, _outbox, () => _hwId, LocalAudit.Write, Power, Messages),
            new WingetInstallHandler(() => _commandRunner, _gate, _outbox, () => _hwId, LocalAudit.Write),
            new CapabilitiesHandler(_vision, Power, () => _tray.Pipe, _channel.TrySendAsync, LocalAudit.Write, _exam.EndIfCapabilityOffAsync),
            new VisionHandler(_vision, _gate, () => _tray.Pipe),
            _exam,
            _files,
            new PowerHandler(Power),
            new UserMessageHandler(Messages),
        }, remoteInput: _remoteInput);

        // capability_denied (bkz. CapabilityGate; dakikada bir sınırı tüm eylemlerde ortak)
        private Task DenyCapabilityAsync(string capability, string action, int? taskId = null, string reason = null, string transferId = null) =>
            _gate.DenyAsync(capability, action, taskId, reason, transferId);

        // Testler içindir: giden komut mesajları sokete yazılmaz, buraya verilir (dönen: gönderildi mi). Uzaktan komutun
        // çalıştırıcısı ve karantina denetimi de (gerçek güvenlik duvarına dokunmayan) sahteleriyle değiştirilebilir.
        internal Func<object, Task<bool>> SendOverride { get => _channel.SendOverride; set => _channel.SendOverride = value; }
        internal CommandRunner CommandRunner { get => _commandRunner; set => _commandRunner = value; }
        internal QuarantineControl Quarantine { get => _quarantine; set => _quarantine = value; }
        internal string HwId { get => _hwId; set => _hwId = value; }
        internal TrayPipeServer TrayPipe => _tray.Pipe;
        internal SoftwareReporter Software { get => _software; set => _software = value; }

        // Görev sonucu ve heartbeat'te bekleyen sonuçlar (bkz. ResultOutbox)
        private Task SendResultAsync(int taskId, object result) => _outbox.SendResultAsync(taskId, result);
        internal Task FlushPendingResultsAsync() => _outbox.FlushPendingResultsAsync();

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
