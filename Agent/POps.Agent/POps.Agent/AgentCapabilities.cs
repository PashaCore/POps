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
    // Kaynak C:\POpsData\secure\capabilities.json (yalnızca SYSTEM/Administrators). Kurulum (MSI TERMINAL_ENABLED /
    // VISION_ENABLED / EXAM_ENABLED) iki yönde de yazar; sunucu yalnızca KAPATABİLİR ("set_capabilities" ... false). Sunucudan
    // gelen "aç" isteği yok sayılır: yeniden açmak yerel yöneticinin işidir (MSI yeniden kurulum / onarım).
    // Dosya yoksa (bu özellikten önceki kurulum) hepsi açıktır; dosyada olmayan yetenek (eski dosya) açıktır; dosya
    // okunamıyorsa hepsi kapalı sayılır.
    [SupportedOSPlatform("windows")]
    public static class AgentCapabilities
    {
        public const string FileName = "capabilities.json";
        public const string Terminal = "terminal_enabled";
        public const string Vision = "vision_enabled";
        public const string Exam = "exam_enabled";
        private static readonly string[] Names = { Terminal, Vision, Exam };

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, bool> State = new Dictionary<string, bool> { [Terminal] = true, [Vision] = true, [Exam] = true };

        public static bool TerminalEnabled { get { lock (Gate) return State[Terminal]; } }
        public static bool VisionEnabled { get { lock (Gate) return State[Vision]; } }
        public static bool ExamEnabled { get { lock (Gate) return State[Exam]; } }

        public static void Load()
        {
            string path = SecureStore.PathOf(FileName);
            string text = SecureStore.Read(path);
            lock (Gate)
            {
                if (text == null)
                {
                    foreach (string name in Names) State[name] = true;
                    POpsHelpers.Log("POLICY", $"{FileName} yok: terminal, Vision ve sınav modu açık (kurulum varsayılanı).");
                    return;
                }
                try
                {
                    JsonObject json = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("nesne değil");
                    foreach (string name in Names)
                        State[name] = !json.TryGetPropertyValue(name, out JsonNode node) || node?.GetValue<bool>() != false;
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
                {
                    // Dosyaya yalnızca SYSTEM/Administrators yazabilir; okunamıyorsa güvenli yöne düşülür
                    foreach (string name in Names) State[name] = false;
                    POpsHelpers.Log("POLICY", $"[GÜVENLİK] {FileName} okunamadı ({ex.Message}); terminal, Vision ve sınav modu kapalı sayılıyor.", true);
                    return;
                }
                POpsHelpers.Log("POLICY", $"Yetenekler: {Describe()}.");
            }
        }

        // Sunucunun "set_capabilities" isteği: yalnızca false değerler uygulanır. Dönüş: kapatılan ve yok sayılan adlar.
        public static (List<string> Disabled, List<string> IgnoredEnables) ApplyServerRequest(JsonElement request)
        {
            var disabled = new List<string>();
            var ignored = new List<string>();
            lock (Gate)
            {
                foreach (string name in Names)
                {
                    if (!request.TryGetProperty(name, out JsonElement value)) continue;
                    if (value.ValueKind == JsonValueKind.False)
                    {
                        if (State[name]) disabled.Add(name);
                        State[name] = false;
                    }
                    else if (value.ValueKind == JsonValueKind.True && !State[name])
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

        // Sunucuya bildirilen durum ({"type":"capabilities", ...}); server_ca: sunucu sertifikası kurum sertifikasıyla
        // ("custom", server-ca.pem) mı, sistem deposuyla ("system") mı doğrulanıyor (bkz. ServerTrust)
        public static Dictionary<string, object> StatusMessage()
        {
            lock (Gate)
                return new Dictionary<string, object> { ["type"] = "capabilities", [Terminal] = State[Terminal], [Vision] = State[Vision], [Exam] = State[Exam], ["server_ca"] = ServerTrust.Mode };
        }

        public static string Describe()
        {
            lock (Gate) return $"terminal={(State[Terminal] ? "açık" : "kapalı")}, vision={(State[Vision] ? "açık" : "kapalı")}, sınav={(State[Exam] ? "açık" : "kapalı")}";
        }

        private static void Persist(string source)
        {
            var json = new JsonObject
            {
                [Terminal] = State[Terminal],
                [Vision] = State[Vision],
                [Exam] = State[Exam],
                ["source"] = source,
                ["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            try { SecureStore.WriteProtected(SecureStore.PathOf(FileName), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { POpsHelpers.Log("POLICY", $"{FileName} yazılamadı; kapatma bu çalışmada geçerli, yeniden başlatmada kaybolabilir: {ex.Message}", true); }
        }
    }
}
