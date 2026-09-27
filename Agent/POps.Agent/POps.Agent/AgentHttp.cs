using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Ajanın sunucuya HTTP bildirimleri (yazılım envanteri, Windows Update durumu, oturum olayları, loglar).
    // Bu uçlar yalnızca anahtarlı ajanı kabul eder (X-Agent-Id + X-Agent-Secret; enforce_agent_auth kapalı olsa
    // bile): cihaz secret'ı yoksa hiçbir şey gönderilmez. Şifresiz ve yerel olmayan sunucuya da gönderilmez.
    public static class AgentHttp
    {
        public static HttpClient Client { get; set; } = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };

        public static bool CanReport => AgentCredentials.CurrentSecret != null;

        private static bool _noSecretLogged;

        // Secret yokken bildirimlerin neden gitmediği bir kez loglanır (ilk kayıtta secret birkaç saniye sonra gelir)
        public static bool EnsureCanReport()
        {
            if (CanReport) { _noSecretLogged = false; return true; }
            if (!_noSecretLogged) POpsHelpers.Log("AGENT", "Cihaz secret'ı yok: yazılım envanteri, Windows Update durumu ve oturum olayları gönderilmiyor (sunucu yalnızca kayıtlı ajanı kabul eder).");
            _noSecretLogged = true;
            return false;
        }

        // path: "/api/software/{hw_id}" gibi. Başarılıysa true; hata loglanır.
        public static async Task<bool> PostJsonAsync(string serverUrl, string path, string hwId, object payload, string what)
        {
            if (!CanReport || !POpsHelpers.IsSecureServerUrl(serverUrl)) return false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + path)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                };
                AgentCredentials.AddHttpAuth(request, hwId);
                using var response = await Client.SendAsync(request);
                if (response.IsSuccessStatusCode) return true;
                POpsHelpers.Log("AGENT", $"{what} gönderilemedi: HTTP {(int)response.StatusCode}.", true);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"{what} gönderilemedi: {ex.Message}", true);
            }
            return false;
        }

        public static string DevicePath(string prefix, string hwId) => prefix + Uri.EscapeDataString(hwId ?? "");
    }

    // Sunucu modeli: Backend/pops/models.py LogInput (/api/logs/{hw_id}; denetim kaydı)
    public sealed class AgentLogPayload
    {
        [JsonPropertyName("log_type")] public string LogType { get; set; } = "System";
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("actor_id")] public string ActorId { get; set; } = "Agent";
        [JsonPropertyName("event_type")] public string EventType { get; set; } = "agent.log";
        [JsonPropertyName("category")] public string Category { get; set; } = "legacy";
        [JsonPropertyName("action")] public string Action { get; set; } = "unknown";
        [JsonPropertyName("risk_level")] public string RiskLevel { get; set; } = "info";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("meta_data")] public Dictionary<string, object> MetaData { get; set; } = new Dictionary<string, object>();
    }

    // "Değişmediyse gönderme, yine de belli aralıkla gönder": son BAŞARIYLA gönderilen özet ve zamanı tutulur.
    public sealed class ReportGate
    {
        private readonly TimeSpan _maxSilence;

        public ReportGate(TimeSpan maxSilence) => _maxSilence = maxSilence;

        public string LastHash { get; private set; }
        public DateTime LastSentUtc { get; private set; } = DateTime.MinValue;

        public bool ShouldSend(string hash, DateTime utcNow) =>
            LastHash == null || !string.Equals(hash, LastHash, StringComparison.Ordinal) || utcNow - LastSentUtc >= _maxSilence;

        public void MarkSent(string hash, DateTime utcNow)
        {
            LastHash = hash;
            LastSentUtc = utcNow;
        }
    }
}
