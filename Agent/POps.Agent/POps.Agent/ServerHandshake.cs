using System;
using System.Collections.Generic;
using System.Text.Json;

#nullable disable

namespace POpsAgent
{
    // Bağlantı başına sunucunun duyurduğu özellikler: {"action":"server_info","version":..,"features":[...]}.
    // Her yeni bağlantıda sıfırlanır; bağlantıdan sonra 15 sn içinde server_info gelmezse sunucu eski sayılır (hiçbir
    // özellik yok). update_result onayı (UpdateResultReporter) ve görev sonucu onayı (ResultSpool) aynı kuralı kullanır.
    public sealed class ServerHandshake
    {
        public static readonly TimeSpan ServerInfoWait = TimeSpan.FromSeconds(15);

        private readonly object _gate = new object();
        private DateTime _connectedUtc = DateTime.MinValue;
        private bool _seen;
        private HashSet<string> _features = new HashSet<string>(StringComparer.Ordinal);
        // En son öğrenilen özellikler (server_info ya da 15 sn dolunca "hiçbiri"); bağlantılar arasında korunur ve yeni
        // bağlantının ilk saniyelerinde (henüz bilinmiyorken) ipucu olarak kullanılır. null: hiç öğrenilmedi.
        private HashSet<string> _lastKnown;

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public void OnConnected()
        {
            lock (_gate)
            {
                _connectedUtc = UtcNow();
                _seen = false;
                _features = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        public void OnServerInfo(JsonElement message)
        {
            var features = new HashSet<string>(StringComparer.Ordinal);
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("features", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement f in list.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.String) features.Add(f.GetString());
            lock (_gate)
            {
                _seen = true;
                _features = features;
                _lastKnown = features;
            }
        }

        // null: henüz bilinmiyor (server_info bekleniyor); false: sunucu bu özelliği duyurmadı ya da eski sunucu
        public bool? Supports(string feature)
        {
            lock (_gate)
            {
                if (_seen) return _features.Contains(feature);
                if (UtcNow() - _connectedUtc < ServerInfoWait) return null;
                // server_info gelmedi: eski sunucu, hiçbir özellik yok
                _lastKnown = new HashSet<string>(StringComparer.Ordinal);
                return false;
            }
        }

        public bool? LastKnown(string feature)
        {
            lock (_gate) return _lastKnown == null ? (bool?)null : _lastKnown.Contains(feature);
        }
    }
}
