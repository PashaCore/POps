using System.Collections.Generic;
using System.Text.Json.Serialization;

#nullable disable

namespace POpsAgent
{
    // GET /api/agent_policies yanıtı (alan adları sunucunun JSON'ı)
    public class AgentPolicy
    {
        [JsonPropertyName("fair_use_text")] public string FairUseText { get; set; } = "";
        [JsonPropertyName("dns_categories")] public List<string> DnsCategories { get; set; } = new List<string>();
        // Kategori -> alan adları (tam eşleşme ya da alt alan; bkz. DnsWatch). Yoksa DNS tespiti yapılmaz.
        [JsonPropertyName("dns_domains")] public Dictionary<string, List<string>> DnsDomains { get; set; } = new Dictionary<string, List<string>>();
        [JsonPropertyName("auto_quarantine")] public bool AutoQuarantine { get; set; }
        [JsonPropertyName("quarantine_threshold")] public int QuarantineThreshold { get; set; } = 3;
    }
}
