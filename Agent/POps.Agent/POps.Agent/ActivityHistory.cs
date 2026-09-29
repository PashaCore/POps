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
    // GET /api/activity/agent/{hw_id} yanıtındaki kayıt. Başlık ve açıklama sunucuda Türkçe hazırlanır; tepsi yorumlamaz.
    public sealed class ActivityItem
    {
        [JsonPropertyName("at")] public string At { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("actor")] public string Actor { get; set; }
        [JsonPropertyName("detail")] public string Detail { get; set; }
    }

    // Servisten tepsiye: "ACTIVITY_LIST_RESULT:<base64 JSON>"
    public sealed class ActivityListMessage
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        // Çok sık istendi (ajan ya da sunucu sınırı): tepsi açık listeyi korur, yalnızca notu gösterir
        [JsonPropertyName("busy")] public bool Busy { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
        [JsonPropertyName("days")] public int Days { get; set; } = ActivityHistory.DefaultDays;
        [JsonPropertyName("items")] public List<ActivityItem> Items { get; set; } = new List<ActivityItem>();
    }

    // "Etkinlik geçmişim": BT yöneticilerinin bu bilgisayarda yaptığı işlemler (uzaktan oturum, komut, karantina,
    // güncelleme, yetenek, Windows Update, kayıt). Tepsi ACTIVITY_LIST gönderir; servis isteği anahtarlı ajan olarak
    // sunucuya iletir ("Taleplerim" ile aynı kimlik akışı).
    //  * Sunucu cihaz başına 5 sn'de bir isteğe izin verir: aynı anda tek istek, iki istek arası en az 6 sn.
    //  * Başarılı yanıt 60 sn önbellekte tutulur; o sürede sunucuya yeniden gidilmez.
    //  * Kayıtlar olduğu gibi gösterilir; yalnızca denetim karakterleri temizlenir ve uzunluklar sınırlanır.
    public sealed class ActivityHistory
    {
        public const int DefaultDays = 30, MaxItems = 200;
        public const int MaxAt = 40, MaxKind = 40, MaxTitle = 200, MaxActor = 100, MaxDetail = 1000;
        public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(6);

        public const string UnsupportedMessage = "Sunucu etkinlik geçmişini henüz desteklemiyor.";
        public const string UnreadableMessage = "Sunucunun yanıtı okunamadı.";
        public const string EmptyMessage = "Son 30 günde işlem yok.";

        private readonly Func<string> _hwId;
        private readonly Action<string> _toTray;
        private readonly object _gate = new object();
        private bool _busy;
        private DateTime _lastRequestUtc = DateTime.MinValue;
        private ActivityListMessage _cached;
        private DateTime _cachedAtUtc;

        public ActivityHistory(string serverUrl, Func<string> hwId, Action<string> toTray)
        {
            _hwId = hwId;
            _toTray = toTray;
            Sender = (method, path, payload, what) => AgentHttp.SendAsync(method, serverUrl, path, _hwId(), payload, what);
        }

        // Testlerde değiştirilir
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
        internal Func<bool> CanReport { get; set; } = () => AgentHttp.CanReport;
        internal Func<HttpMethod, string, object, string, Task<(int? Status, string Body)>> Sender { get; set; }

        public async Task ListAsync()
        {
            ActivityListMessage result = await GetAsync();
            _toTray?.Invoke("ACTIVITY_LIST_RESULT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result))));
        }

        internal async Task<ActivityListMessage> GetAsync()
        {
            if (!CanReport()) return Failure(Helpdesk.NotEnrolledMessage);
            lock (_gate)
            {
                DateTime now = UtcNow();
                if (_cached != null && now - _cachedAtUtc < CacheFor) return _cached;
                if (_busy || now - _lastRequestUtc < MinInterval) return Failure(Helpdesk.BusyMessage, busy: true);
                _busy = true;
                _lastRequestUtc = now;
            }
            try
            {
                var (status, body) = await Sender(HttpMethod.Get, AgentHttp.DevicePath("/api/activity/agent/", _hwId()), null, "Etkinlik geçmişi");
                ActivityListMessage result = FromResponse(status, body);
                if (result.Ok)
                    lock (_gate)
                    {
                        _cached = result;
                        _cachedAtUtc = UtcNow();
                    }
                return result;
            }
            finally
            {
                lock (_gate) _busy = false;
            }
        }

        // ------------------------------------------------------------------ saf mantık (testlerde denenir)

        public static ActivityListMessage FromResponse(int? status, string body)
        {
            if (status == null) return Failure(Helpdesk.UnreachableMessage);
            if (status == 429) return Failure(Helpdesk.BusyMessage, busy: true);
            if (status == 401 || status == 403) return Failure(Helpdesk.NotEnrolledMessage);
            if (status == 404 || status == 405) return Failure(UnsupportedMessage);
            if (status < 200 || status >= 300 || body == null) return Failure($"Etkinlik geçmişi alınamadı (HTTP {status}).");
            return Parse(body) ?? Failure(UnreadableMessage);
        }

        // Geçersiz JSON ya da "items" dizisi yoksa null. Nesne olmayan kayıt atlanır; eksik alan boş/null kalır,
        // bilinmeyen kind da gösterilir. En yeni başta gelir; en çok MaxItems kayıt.
        public static ActivityListMessage Parse(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body ?? "");
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                    return null;
                var result = new ActivityListMessage { Ok = true };
                if (root.TryGetProperty("days", out JsonElement d) && d.TryGetInt32(out int days) && days > 0) result.Days = days;
                foreach (JsonElement e in items.EnumerateArray())
                {
                    if (result.Items.Count == MaxItems) break;
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    string kind = Text(e, "kind", MaxKind) ?? "";
                    result.Items.Add(new ActivityItem
                    {
                        At = Text(e, "at", MaxAt) ?? "",
                        Kind = kind,
                        Title = Text(e, "title", MaxTitle) ?? (kind.Length > 0 ? kind : "İşlem"),
                        Actor = Text(e, "actor", MaxActor),
                        Detail = Text(e, "detail", MaxDetail),
                    });
                }
                if (result.Items.Count == 0) result.Message = result.Days == DefaultDays ? EmptyMessage : $"Son {result.Days} günde işlem yok.";
                return result;
            }
            catch (JsonException) { return null; }
        }

        private static string Text(JsonElement e, string name, int max)
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.String) return null;
            string text = Clean(v.GetString() ?? "", max);
            return text.Length == 0 ? null : text;
        }

        // Ekranda gösterilecek metin: satır sonu ve sekme tek boşluk olur; denetim ve biçim karakterleri (yön
        // değiştiren bidi işaretleri dahil) atılır; uzun metin kısaltılır
        internal static string Clean(string value, int max)
        {
            var sb = new StringBuilder(Math.Min(value.Length, max + 1));
            foreach (char c in value)
            {
                if (sb.Length >= max) { sb.Append('…'); break; }
                if (char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); }
                else if (!char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private static ActivityListMessage Failure(string message, bool busy = false) =>
            new ActivityListMessage { Ok = false, Busy = busy, Message = message };
    }
}
