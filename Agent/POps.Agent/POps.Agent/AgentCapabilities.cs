using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

#nullable disable

namespace POpsAgent
{
    // Yetenek politikası (SECURITY.md tehdit #4): okul, sunucudan gelen en tehlikeli yetenekleri kalıcı olarak
    // kapatabilir. Sunucu ele geçirilse bile kapalı bir yetenek o makinede kullanılamaz.
    //  * terminal_enabled: "execute" (SYSTEM olarak komut çalıştırma)
    //  * vision_enabled:   ekran akışı, ekran önizlemesi, uzaktan fare/klavye
    //  * exam_enabled:     sınav modu (ağı izin listesiyle sınırlama, uygulama engeli; bkz. ExamMode)
    //  * files_enabled:    dosya gönderme ve alma (bkz. FileTransfer)
    //  * power_enabled:    "power" (kapatma, yeniden başlatma, oturum kapatma, kilitleme; bkz. PowerActions)
    //  * message_enabled:  "user_message" (tepside kullanıcıya mesaj; bkz. UserMessages)
    //  * peer_cache_enabled: laboratuvar eş önbelleği; güncelleme paketini eşlere sunma ve eşlerden indirme (bkz. PeerCache)
    // Kaynak C:\POpsData\secure\capabilities.json (yalnızca SYSTEM/Administrators). Kurulum (MSI TERMINAL_ENABLED /
    // VISION_ENABLED / EXAM_ENABLED / FILES_ENABLED / POWER_ENABLED / MESSAGE_ENABLED / PEER_CACHE_ENABLED) iki yönde de
    // yazar; sunucu yalnızca KAPATABİLİR ("set_capabilities" ... false). Sunucudan
    // gelen "aç" isteği yok sayılır: yeniden açmak yerel yöneticinin işidir (MSI yeniden kurulum / onarım).
    // Dosya yoksa (bu özellikten önceki kurulum) hepsi açıktır; dosyada olmayan yetenek (eski dosya) açıktır; dosya
    // okunamıyorsa hepsi kapalı sayılır.
    // Bu çalışmanın durumu bağlamdaki CapabilityState örneğindedir (bkz. AgentContext.Capabilities); burada adlar ve süreç
    // varsayılanı kalır.
    [SupportedOSPlatform("windows")]
    public static class AgentCapabilities
    {
        public const string FileName = "capabilities.json";
        public const string Terminal = "terminal_enabled";
        public const string Vision = "vision_enabled";
        public const string Exam = "exam_enabled";
        public const string Files = "files_enabled";
        public const string Power = "power_enabled";
        public const string Message = "message_enabled";
        public const string PeerCacheKey = "peer_cache_enabled";
        internal static readonly string[] Names = { Terminal, Vision, Exam, Files, Power, Message, PeerCacheKey };

        // Süreç varsayılanı: servis açılışta bağlamın örneğine çevirir (bkz. AgentContext.UseForProcess). Worker ve kurduğu
        // servisler bağlamın örneğini kullanır; bunu Worker bölmesinin b3 adımına kadar statik kalan kod (PeerCache,
        // AgentFeatures.Header, AgentUpdate'in eş önbelleği kararları) ve aşağıdaki biçimleri çağıran SharedState testleri
        // okur. Başlangıç değeri dosyayı (ve kurum sertifikasını) her kullanımda SecureStore.Dir'den alır.
        public static CapabilityState Process { get; internal set; } =
            new CapabilityState(() => SecureStore.PathOf(FileName), new ServerTrust(() => SecureStore.PathOf(ServerTrust.FileName)));

        public static bool TerminalEnabled => Process.TerminalEnabled;
        public static bool VisionEnabled => Process.VisionEnabled;
        public static bool ExamEnabled => Process.ExamEnabled;
        public static bool FilesEnabled => Process.FilesEnabled;
        public static bool PowerEnabled => Process.PowerEnabled;
        public static bool MessageEnabled => Process.MessageEnabled;
        public static bool PeerCacheEnabled => Process.PeerCacheEnabled;
        public static void Load() => Process.Load();
        public static (List<string> Disabled, List<string> IgnoredEnables) ApplyServerRequest(JsonElement request) => Process.ApplyServerRequest(request);
        public static Dictionary<string, object> StatusMessage() => Process.StatusMessage();
    }

    // Bir ajanın yetenek durumu (bkz. AgentCapabilities): capabilities.json'dan okunur, sunucunun kapatma isteğiyle değişir.
    // Servis için Program bir kez kurar, testler kendi geçici klasörleriyle kurar.
    [SupportedOSPlatform("windows")]
    public sealed class CapabilityState
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, bool> _state = new Dictionary<string, bool>
        {
            [AgentCapabilities.Terminal] = true, [AgentCapabilities.Vision] = true, [AgentCapabilities.Exam] = true, [AgentCapabilities.Files] = true,
            [AgentCapabilities.Power] = true, [AgentCapabilities.Message] = true, [AgentCapabilities.PeerCacheKey] = true,
        };
        // capabilities.json (servis: <güvenli depo>\capabilities.json) ve sunucu sertifikasının doğrulanma kipi ("server_ca")
        private readonly Func<string> _path;
        private readonly ServerTrust _trust;

        // path: capabilities.json; trust: bağlamın ServerTrust'ı ("server_ca")
        public CapabilityState(string path, ServerTrust trust)
        {
            ArgumentNullException.ThrowIfNull(path);
            _path = () => path;
            _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        }

        // Dosya her kullanımda okunur: süreç varsayılanı içindir (bkz. AgentCapabilities.Process)
        internal CapabilityState(Func<string> path, ServerTrust trust)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        }

        public bool TerminalEnabled { get { lock (_gate) return _state[AgentCapabilities.Terminal]; } }
        public bool VisionEnabled { get { lock (_gate) return _state[AgentCapabilities.Vision]; } }
        public bool ExamEnabled { get { lock (_gate) return _state[AgentCapabilities.Exam]; } }
        public bool FilesEnabled { get { lock (_gate) return _state[AgentCapabilities.Files]; } }
        public bool PowerEnabled { get { lock (_gate) return _state[AgentCapabilities.Power]; } }
        public bool MessageEnabled { get { lock (_gate) return _state[AgentCapabilities.Message]; } }
        public bool PeerCacheEnabled { get { lock (_gate) return _state[AgentCapabilities.PeerCacheKey]; } }

        public void Load()
        {
            string path = _path();
            string text = SecureStore.Read(path);
            lock (_gate)
            {
                if (text == null)
                {
                    foreach (string name in AgentCapabilities.Names) _state[name] = true;
                    POpsHelpers.Log("POLICY", $"{AgentCapabilities.FileName} yok: bütün yetenekler açık (kurulum varsayılanı).");
                    return;
                }
                try
                {
                    JsonObject json = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("nesne değil");
                    foreach (string name in AgentCapabilities.Names)
                        _state[name] = !json.TryGetPropertyValue(name, out JsonNode node) || node?.GetValue<bool>() != false;
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
                {
                    // Dosyaya yalnızca SYSTEM/Administrators yazabilir; okunamıyorsa güvenli yöne düşülür
                    foreach (string name in AgentCapabilities.Names) _state[name] = false;
                    POpsHelpers.Log("POLICY", $"[GÜVENLİK] {AgentCapabilities.FileName} okunamadı ({ex.Message}); bütün yetenekler kapalı sayılıyor.", true);
                    return;
                }
                POpsHelpers.Log("POLICY", $"Yetenekler: {Describe()}.");
            }
        }

        // Sunucunun "set_capabilities" isteği: yalnızca false değerler uygulanır. Dönüş: kapatılan ve yok sayılan adlar.
        public (List<string> Disabled, List<string> IgnoredEnables) ApplyServerRequest(JsonElement request)
        {
            var disabled = new List<string>();
            var ignored = new List<string>();
            lock (_gate)
            {
                foreach (string name in AgentCapabilities.Names)
                {
                    if (!request.TryGetProperty(name, out JsonElement value)) continue;
                    if (value.ValueKind == JsonValueKind.False)
                    {
                        if (_state[name]) disabled.Add(name);
                        _state[name] = false;
                    }
                    else if (value.ValueKind == JsonValueKind.True && !_state[name])
                    {
                        ignored.Add(name);
                    }
                }
                if (disabled.Count > 0) Persist("server");
            }
            if (disabled.Count > 0)
                POpsHelpers.Log("POLICY", $"[GÜVENLİK] Sunucu yetenek kapattı: {string.Join(", ", disabled)}. Yeniden açmak yalnızca yerel yönetici kurulumuyla mümkün. Yetenekler: {Describe()}.");
            if (ignored.Count > 0)
                POpsHelpers.Log("POLICY", $"[GÜVENLİK] Sunucunun açma isteği yok sayıldı: {string.Join(", ", ignored)} (yalnızca MSI yeniden kurulum / onarım açabilir).", true);
            return (disabled, ignored);
        }

        // Sunucuya bildirilen durum ({"type":"capabilities", ...}; docs/protocol/agent-to-server/capabilities.json);
        // server_ca: sunucu sertifikası kurum sertifikasıyla ("custom", server-ca.pem) mı, sistem deposuyla ("system")
        // mı doğrulanıyor (bkz. ServerTrust). Yedi yeteneğin hepsi bildirilir; exam_enabled, power_enabled,
        // message_enabled ve peer_cache_enabled şemada isteğe bağlıdır (eski ajan göndermez).
        public Dictionary<string, object> StatusMessage()
        {
            lock (_gate)
                return new Dictionary<string, object>
                {
                    ["type"] = "capabilities",
                    [AgentCapabilities.Terminal] = _state[AgentCapabilities.Terminal],
                    [AgentCapabilities.Vision] = _state[AgentCapabilities.Vision],
                    ["server_ca"] = _trust.Mode,
                    [AgentCapabilities.Files] = _state[AgentCapabilities.Files],
                    [AgentCapabilities.Exam] = _state[AgentCapabilities.Exam],
                    [AgentCapabilities.Power] = _state[AgentCapabilities.Power],
                    [AgentCapabilities.Message] = _state[AgentCapabilities.Message],
                    [AgentCapabilities.PeerCacheKey] = _state[AgentCapabilities.PeerCacheKey],
                };
        }

        public string Describe()
        {
            lock (_gate)
                return $"terminal={OnOff(AgentCapabilities.Terminal)}, vision={OnOff(AgentCapabilities.Vision)}, sınav={OnOff(AgentCapabilities.Exam)}, dosya={OnOff(AgentCapabilities.Files)}, güç={OnOff(AgentCapabilities.Power)}, mesaj={OnOff(AgentCapabilities.Message)}, eş önbelleği={OnOff(AgentCapabilities.PeerCacheKey)}";
        }

        private string OnOff(string name) => _state[name] ? "açık" : "kapalı";

        private void Persist(string source)
        {
            var json = new JsonObject();
            foreach (string name in AgentCapabilities.Names) json[name] = _state[name];
            json["source"] = source;
            json["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try { SecureStore.WriteProtected(_path(), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { POpsHelpers.Log("POLICY", $"{AgentCapabilities.FileName} yazılamadı; kapatma bu çalışmada geçerli, yeniden başlatmada kaybolabilir: {ex.Message}", true); }
        }
    }
}
