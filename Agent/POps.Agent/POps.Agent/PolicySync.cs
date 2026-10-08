using System;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Sunucu politikası: dakikada bir GET /api/agent_policies (DNS ayarı, modül listesi, aydınlatma metni), modül
    // değişikliğine tepki ve tepsinin yardım masası menüsü. DNS politika izleme komut tüneli kurulunca son politikayla başlar.
    [SupportedOSPlatform("windows")]
    internal sealed class PolicySync
    {
        private static readonly JsonSerializerOptions PolicyJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private readonly string _serverUrl;
        private readonly Func<string?> _hwId;
        private readonly HttpClient _httpClient;
        private readonly AgentHealthTelemetry _health;
        private readonly VisionSession _vision;
        private readonly Func<TrayPipeServer?> _trayPipe;
        private readonly Func<SoftwareReporter?> _software;
        private readonly Action<LocalAuditEvent> _audit;

        private AgentPolicy _currentPolicy = new AgentPolicy();
        private bool _fairUseAcknowledged;

        // Kimlik, tepsi borusu ve yazılım envanteri çalışırken değişir (set_identity, servis açılışı, testler): her
        // kullanımda okunur. httpClient: anahtarsız politika isteği (sunucu sertifikası ServerTrust ile doğrulanır);
        // audit: modül değişikliğinin Olay Günlüğü kaydı
        public PolicySync(string serverUrl, Func<string?> hwId, HttpClient httpClient, AgentHealthTelemetry health, VisionSession vision,
            Func<TrayPipeServer?> trayPipe, Func<SoftwareReporter?> software, Action<LocalAuditEvent> audit)
        {
            _serverUrl = serverUrl;
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _health = health ?? throw new ArgumentNullException(nameof(health));
            _vision = vision ?? throw new ArgumentNullException(nameof(vision));
            _trayPipe = trayPipe ?? throw new ArgumentNullException(nameof(trayPipe));
            _software = software ?? throw new ArgumentNullException(nameof(software));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }

        // Kullanıcı aydınlatma metnini onayladı (tepsi: FAIR_USE_ACK); bu çalışmada yeniden gösterilmez
        internal void AcknowledgeFairUse() => _fairUseAcknowledged = true;

        // Komut tüneli kuruldu: DNS politika izleme başlar (yalnızca ilk bağlantıda; sonra açık kalır). Politika ve
        // dns_domains her dakika PolicyPollingLoop'ta yenilenir.
        internal void OnCommandChannelConnected()
        {
            DnsPolicyMonitor.Configure(_currentPolicy, _hwId(), _serverUrl);
            DnsPolicyMonitor.Start();
        }

        internal async Task PolicyPollingLoop(CancellationToken token)
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
                var (status, body) = await AgentHttp.SendAsync(HttpMethod.Get, _serverUrl, "/api/agent_policies", _hwId(), null, "Politika");
                if (status >= 200 && status < 300 && body != null) return body;
                throw new HttpRequestException(status == null ? "yanıt yok" : $"HTTP {status}");
            }
            return await _httpClient.GetStringAsync(_serverUrl.TrimEnd('/') + "/api/agent_policies", token);
        }

        internal void ApplyPolicy(string json)
        {
            var policy = JsonSerializer.Deserialize<AgentPolicy>(json, PolicyJson);
            if (policy == null) return;
            using (JsonDocument doc = JsonDocument.Parse(json)) OnModulesChanged(AgentModules.Apply(doc.RootElement));
            _currentPolicy = policy;
            DnsPolicyMonitor.Configure(policy, _hwId(), _serverUrl);
            _health.PolicySynced();

            if (!string.IsNullOrWhiteSpace(policy.FairUseText) && !_fairUseAcknowledged)
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(policy.FairUseText));
                _trayPipe()?.SendCommandToDesktop($"SHOW_FAIR_USE:{b64}");
            }
        }

        // Modül açıldı/kapandı: bir kez loglanır ve Olay Günlüğüne yazılır (servis açılışındaki ilk yanıtta yalnızca
        // kapalı modül varsa). Kapanan Vision oturumu kesilir, tepsinin yardım masası menüsü eşitlenir, yeniden açılan
        // yazılım envanterinin son gönderimi unutulur (kapalıyken sunucu listeyi saklamadı).
        internal void OnModulesChanged(ModuleChange? change)
        {
            if (change == null || !change.Any) return;
            string closed = change.Closed.Count > 0 ? string.Join(", ", change.Closed) : "-";
            string opened = change.Opened.Count > 0 ? string.Join(", ", change.Opened) : "-";
            POpsHelpers.Log("AGENT", change.First
                ? $"Sunucu bu bilgisayarın laboratuvarında şu modülleri kapattı: {closed}."
                : $"Sunucu modülleri değişti: kapatılan {closed}; açılan {opened}.");
            _audit(LocalAudit.ModulesChanged(change.Closed, change.Opened));

            if (change.Closed.Contains(AgentModules.Vision) && (_vision.StreamActive || _vision.HasTunnel))
            {
                _vision.RevokeApproval();
                _trayPipe()?.SendCommandToDesktop("STOP_CAPTURE");
                _ = _vision.DisconnectVisionTunnelAsync();
            }
            if (change.Closed.Contains(AgentModules.Helpdesk) || change.Opened.Contains(AgentModules.Helpdesk)) SyncTrayModules();
            if (change.Opened.Contains(AgentModules.Software)) _software()?.ForgetLastReport();
        }

        // Tepside "Sorun bildir" ve "Taleplerim" yalnızca yardım masası modülü açıkken görünür
        internal static string HelpdeskMenuMessage() => "HELPDESK_MENU:" + (AgentModules.IsEnabled(AgentModules.Helpdesk) ? "1" : "0");

        internal void SyncTrayModules() => _trayPipe()?.SendCommandToDesktop(HelpdeskMenuMessage());
    }
}
