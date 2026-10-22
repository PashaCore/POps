using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Sunucu modeli: Backend/pops/models.py AgentTicketInput
    public sealed class TicketCreatePayload
    {
        [JsonPropertyName("subject")] public string Subject { get; set; }
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "diger";
        [JsonPropertyName("reporter")] public string Reporter { get; set; }
    }

    // GET /api/tickets/agent/{hw_id} yanıtındaki talep (Backend/pops/routers/helpdesk.py agent_list_tickets)
    public sealed class TicketView
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; }
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; }
        [JsonPropertyName("subject")] public string Subject { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }
        [JsonPropertyName("status_text")] public string StatusText { get; set; }
        [JsonPropertyName("reporter")] public string Reporter { get; set; }
        [JsonPropertyName("replies")] public List<TicketReply> Replies { get; set; } = new List<TicketReply>();
    }

    public sealed class TicketReply
    {
        [JsonPropertyName("author")] public string Author { get; set; }
        [JsonPropertyName("body")] public string Body { get; set; }
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; }
    }

    // Servisten tepsiye giden yanıtlar (boru üzerinden "TICKET_RESULT:<base64 JSON>" vb.)
    public sealed class TicketResultMessage
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
    }

    public sealed class TicketListMessage
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        // Çok sık istendi (ajan ya da sunucu sınırı): tepsi açık listeyi korur, yalnızca notu gösterir
        [JsonPropertyName("busy")] public bool Busy { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; }
        [JsonPropertyName("tickets")] public List<TicketView> Tickets { get; set; } = new List<TicketView>();
    }

    public sealed class TicketNotifyMessage
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("subject")] public string Subject { get; set; }
        [JsonPropertyName("status_text")] public string StatusText { get; set; }
        [JsonPropertyName("new_replies")] public int NewReplies { get; set; }
    }

    // Yardım masası, servis tarafı. Tepsi "Sorun bildir" ile TICKET_CREATE:<base64 JSON {subject, category, body}>,
    // "Taleplerim" ile TICKET_LIST gönderir (yalnızca doğrulanmış tepsi bağlanabilir, bkz. PipeClientVerifier).
    //  * Kullanıcı adı formdan alınmaz: servis konsoldaki oturumun kullanıcısını "reporter" olarak koyar.
    //  * Borudan gelen içerik sınırlanır: konu 200, açıklama 5000 karakter; kategori bilinmiyorsa "diger".
    //  * Sunucu uçları yalnızca anahtarlı ajanı kabul eder; secret yoksa hiç gönderilmez.
    //  * "Taleplerim" yalnızca oturumdaki kullanıcının taleplerini gösterir: sunucu cihazın bütün taleplerini döner,
    //    ortak laboratuvar bilgisayarında bir öğrenci başkasının talebini ve yanıtlarını görmemeli.
    //  * Tepsi mesajlarına güvenilmeyen girdi gibi davranılır (tepsi kullanıcının ortamıyla başlar; kullanıcı kendi kodunu
    //    onun içinde çalıştırabilir): her tür için aynı anda tek istek, liste için en az 6 sn, talep için en az 10 sn
    //    aralık; fazlası sunucuya gitmez, tepsiye "meşgul" döner. Yoklama da liste sınırına tabidir (sunucu cihaz
    //    başına 5 sn'den sık isteğe 429 döner).
    //  * 5 dk'da bir yeni yanıt yoklanır; yeni yanıt tepsiye TICKET_NOTIFY ile bildirilir (balon). Görülen yanıt
    //    sayıları C:\POpsData\tickets-seen.json'da tutulur (servis yeniden başlayınca eski yanıtlar yeniden bildirilmez).
    [SupportedOSPlatform("windows")]
    public sealed class Helpdesk
    {
        public const int MinSubject = 3, MaxSubject = 200, MaxBody = 5000, MaxReporter = 100;
        // Borudan gelen TICKET_CREATE'in base64 kısmı (5000 karakterlik açıklama UTF-8 + JSON + base64 bunun altında kalır)
        public const int MaxEncodedRequest = 64 * 1024;
        public static readonly string[] Categories = { "donanim", "yazilim", "ag", "yazici", "hesap", "diger" };
        public static readonly TimeSpan FirstPoll = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

        public const string NotEnrolledMessage = "Bu bilgisayar sunucuya kayıtlı değil; talep gönderilemedi. BT ekibine haber verin.";
        public const string UnreachableMessage = "Sunucuya ulaşılamadı; biraz sonra yeniden deneyin.";
        public const string BusyMessage = "Çok sık istek; birkaç saniye sonra yeniden deneyin.";
        public const string ModuleDisabledMessage = "Yardım masası bu bilgisayarın laboratuvarında kapalı.";

        // Sunucuda yardım masası modülü (bkz. AgentModules)
        private static bool Enabled => AgentModules.IsEnabled(AgentModules.Helpdesk);
        public static readonly TimeSpan CreateInterval = TimeSpan.FromSeconds(10);
        // Sunucunun sınırı 5 sn: aynı aralık saat kaymasıyla ara sıra 429 alıyordu
        public static readonly TimeSpan ListInterval = TimeSpan.FromSeconds(6);

        // Tür başına: aynı anda tek istek ve iki istek arası en az süre
        private sealed class RequestGate
        {
            public readonly TimeSpan Interval;
            public bool Busy;
            public DateTime LastUtc = DateTime.MinValue;
            public RequestGate(TimeSpan interval) => Interval = interval;
        }

        private readonly RequestGate _createGate = new RequestGate(CreateInterval);
        private readonly RequestGate _listGate = new RequestGate(ListInterval);

        // Testlerde değiştirilir
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        private bool TryEnter(RequestGate gate)
        {
            lock (gate)
            {
                DateTime now = UtcNow();
                if (gate.Busy || now - gate.LastUtc < gate.Interval) return false;
                gate.Busy = true;
                gate.LastUtc = now;
                return true;
            }
        }

        private static void Exit(RequestGate gate)
        {
            lock (gate) gate.Busy = false;
        }

        private readonly string _serverUrl;
        private readonly Func<string> _hwId;
        private readonly Func<string> _currentUser;
        private readonly Action<string> _toTray;
        private readonly object _seenLock = new object();

        // paths: görüntülenen yanıtların kaydı (<veri klasörü>\tickets-seen.json)
        public Helpdesk(AgentPaths paths, string serverUrl, Func<string> hwId, Func<string> currentUser, Action<string> toTray)
        {
            SeenPath = paths.DataFile(SeenFileName);
            _serverUrl = serverUrl;
            _hwId = hwId;
            _currentUser = currentUser;
            _toTray = toTray;
            Sender = (method, path, payload, what) => AgentHttp.SendAsync(method, _serverUrl, path, _hwId(), payload, what);
        }

        // Testlerde değiştirilir: sunucuya istek (durum kodu ve gövde; ağ hatasında null)
        internal Func<HttpMethod, string, object, string, Task<(int? Status, string Body)>> Sender { get; set; }

        public const string SeenFileName = "tickets-seen.json";

        public string SeenPath { get; }

        private string DevicePath => AgentHttp.DevicePath("/api/tickets/agent/", _hwId());

        // ------------------------------------------------------------------ tepsi istekleri

        public async Task CreateAsync(string encoded)
        {
            if (!Enabled)
            {
                Reply("TICKET_RESULT", new TicketResultMessage { Ok = false, Message = ModuleDisabledMessage });
                return;
            }
            TicketCreatePayload payload = ParseCreate(encoded, _currentUser(), out string error);
            if (payload == null)
            {
                Reply("TICKET_RESULT", new TicketResultMessage { Ok = false, Message = error });
                return;
            }
            if (!AgentHttp.CanReport)
            {
                Reply("TICKET_RESULT", new TicketResultMessage { Ok = false, Message = NotEnrolledMessage });
                return;
            }
            if (!TryEnter(_createGate))
            {
                Reply("TICKET_RESULT", new TicketResultMessage { Ok = false, Message = BusyMessage });
                return;
            }
            try
            {
                var (status, body) = await Sender(HttpMethod.Post, DevicePath, payload, "Destek talebi");
                TicketResultMessage result = CreateResult(status, body);
                if (result.Ok) POpsHelpers.Log("AGENT", $"Destek talebi gönderildi (#{result.Id}, {payload.Category}).");
                Reply("TICKET_RESULT", result);
            }
            finally { Exit(_createGate); }
        }

        public async Task ListAsync()
        {
            string user = _currentUser();
            if (!Enabled)
            {
                Reply("TICKET_LIST_RESULT", new TicketListMessage { Ok = false, Message = ModuleDisabledMessage });
                return;
            }
            if (!AgentHttp.CanReport)
            {
                Reply("TICKET_LIST_RESULT", new TicketListMessage { Ok = false, Message = NotEnrolledMessage });
                return;
            }
            if (!TryEnter(_listGate))
            {
                // Tepsi açık listeyi korur, yalnızca bu kısa notu gösterir
                Reply("TICKET_LIST_RESULT", new TicketListMessage { Ok = false, Message = BusyMessage, Busy = true });
                return;
            }
            List<TicketView> tickets;
            string message;
            bool rateLimited;
            try { (tickets, message, rateLimited) = await FetchAsync(); }
            finally { Exit(_listGate); }
            if (tickets == null)
            {
                Reply("TICKET_LIST_RESULT", new TicketListMessage { Ok = false, Message = message, Busy = rateLimited });
                return;
            }
            List<TicketView> mine = ForUser(tickets, user);
            // Kullanıcı listeyi gördü: bu yanıtlar için ayrıca balon çıkmaz
            lock (_seenLock)
            {
                Dictionary<long, int> seen = LoadSeen();
                foreach (TicketView t in mine) seen[t.Id] = t.Replies?.Count ?? 0;
                SaveSeen(seen);
            }
            Reply("TICKET_LIST_RESULT", new TicketListMessage { Ok = true, Tickets = mine, Message = mine.Count == 0 ? "Henüz talebiniz yok." : null });
        }

        // Yeni yanıt yoklaması. Tepsi bağlıyken ve oturumda kullanıcı varken.
        public async Task PollLoopAsync(CancellationToken token, Func<bool> trayConnected)
        {
            try { await Task.Delay(FirstPoll, token); }
            catch (TaskCanceledException) { return; }
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string user = _currentUser();
                    // Liste sınırını tepsiyle paylaşır; o sırada liste isteniyorsa bu tur atlanır. 429 sessizce geçilir.
                    if (user != null && trayConnected() && AgentHttp.CanReport && Enabled && TryEnter(_listGate))
                    {
                        List<TicketView> tickets;
                        try { (tickets, _, _) = await FetchAsync(); }
                        finally { Exit(_listGate); }
                        if (tickets != null)
                        {
                            List<TicketView> fresh;
                            lock (_seenLock)
                            {
                                Dictionary<long, int> seen = LoadSeen();
                                fresh = NewReplies(ForUser(tickets, user), seen);
                                SaveSeen(seen);
                            }
                            foreach (TicketView t in fresh)
                                Reply("TICKET_NOTIFY", new TicketNotifyMessage { Id = t.Id, Subject = t.Subject, StatusText = t.StatusText, NewReplies = t.Replies.Count });
                        }
                    }
                }
                catch (Exception ex) { POpsHelpers.Log("AGENT", $"Destek talepleri yoklanamadı: {ex.Message}", true); }
                try { await Task.Delay(PollInterval, token); }
                catch (TaskCanceledException) { return; }
            }
        }

        // RateLimited: sunucu 429 döndü (cihaz başına 5 sn sınırı); hata sayılmaz
        private async Task<(List<TicketView> Tickets, string Message, bool RateLimited)> FetchAsync()
        {
            var (status, body) = await Sender(HttpMethod.Get, DevicePath, null, "Destek talepleri");
            if (status == null) return (null, UnreachableMessage, false);
            if (status == 429) return (null, BusyMessage, true);
            if (status == 401 || status == 403) return (null, NotEnrolledMessage, false);
            if (status == 404 || status == 405) return (null, "Sunucu yardım masasını henüz desteklemiyor.", false);
            if (status < 200 || status >= 300 || body == null) return (null, $"Talepler alınamadı (HTTP {status}).", false);
            try { return (JsonSerializer.Deserialize<List<TicketView>>(body) ?? new List<TicketView>(), null, false); }
            catch (JsonException) { return (null, "Sunucunun yanıtı okunamadı.", false); }
        }

        private void Reply(string kind, object message) =>
            _toTray?.Invoke(kind + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message))));

        // ------------------------------------------------------------------ saf mantık (testlerde denenir)

        // Tepsinin isteği; geçersizse null ve kullanıcıya gösterilecek hata
        public static TicketCreatePayload ParseCreate(string encoded, string reporter, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedRequest)
            {
                error = "Talep okunamadı ya da çok uzun.";
                return null;
            }
            string subject, body, category;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                JsonElement root = doc.RootElement;
                subject = Text(root, "subject");
                body = Text(root, "body");
                category = Text(root, "category");
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidOperationException)
            {
                error = "Talep okunamadı.";
                return null;
            }

            subject = SoftwareInventory.Clip((subject ?? "").Trim(), MaxSubject);
            if (subject.Length < MinSubject)
            {
                error = "Konu en az 3 karakter olmalı.";
                return null;
            }
            return new TicketCreatePayload
            {
                Subject = subject,
                Body = SoftwareInventory.Clip((body ?? "").Trim(), MaxBody),
                Category = Categories.Contains(category) ? category : "diger",
                Reporter = string.IsNullOrWhiteSpace(reporter) ? null : SoftwareInventory.Clip(reporter.Trim(), MaxReporter),
            };
        }

        private static string Text(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // Sunucunun yanıtı -> kullanıcıya kısa Türkçe mesaj
        public static TicketResultMessage CreateResult(int? status, string body)
        {
            string detail = ServerDetail(body);
            if (status >= 200 && status < 300)
            {
                long? id = null;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(body ?? "{}");
                    if (doc.RootElement.TryGetProperty("id", out JsonElement v) && v.TryGetInt64(out long n)) id = n;
                }
                catch (JsonException) { }
                return new TicketResultMessage
                {
                    Ok = true,
                    Id = id,
                    Message = id.HasValue
                        ? $"Talebiniz alındı (#{id}). BT ekibi yanıt verince bildirim göreceksiniz."
                        : "Talebiniz alındı. BT ekibi yanıt verince bildirim göreceksiniz.",
                };
            }
            string message = status switch
            {
                null => UnreachableMessage,
                400 => detail ?? "Konu en az 3 karakter olmalı.",
                429 => detail ?? "Bu bilgisayardan çok fazla açık talep var; BT ekibinin yanıtını bekleyin.",
                401 or 403 => NotEnrolledMessage,
                404 or 405 => "Sunucu yardım masasını henüz desteklemiyor.",
                _ => $"Talep gönderilemedi (HTTP {status}).",
            };
            return new TicketResultMessage { Ok = false, Message = message };
        }

        // FastAPI hata gövdesi {"detail": "..."}: yalnızca metin, kısaltılmış ve denetim karakterlerinden arındırılmış
        private static string ServerDetail(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body ?? "");
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out JsonElement d) && d.ValueKind == JsonValueKind.String)
                {
                    string text = d.GetString()?.Trim();
                    return string.IsNullOrEmpty(text) ? null : LogText.Safe(text, 200);
                }
            }
            catch (JsonException) { }
            return null;
        }

        // Oturumdaki kullanıcının talepleri (büyük/küçük harf duyarsız); kullanıcı yoksa hiçbiri
        public static List<TicketView> ForUser(IEnumerable<TicketView> tickets, string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return new List<TicketView>();
            return tickets.Where(t => string.Equals(t.Reporter?.Trim(), user.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Görülenden fazla yanıtı olan talepler; ilk kez görülen talep bildirilmez (yeni açılmış ya da eski). seen güncellenir.
        public static List<TicketView> NewReplies(IEnumerable<TicketView> tickets, IDictionary<long, int> seen)
        {
            var fresh = new List<TicketView>();
            foreach (TicketView t in tickets)
            {
                int count = t.Replies?.Count ?? 0;
                if (seen.TryGetValue(t.Id, out int known) && count > known) fresh.Add(t);
                seen[t.Id] = count;
            }
            return fresh;
        }

        internal Dictionary<long, int> LoadSeen()
        {
            try
            {
                if (File.Exists(SeenPath)) return JsonSerializer.Deserialize<Dictionary<long, int>>(File.ReadAllText(SeenPath)) ?? new Dictionary<long, int>();
            }
            catch { }
            return new Dictionary<long, int>();
        }

        internal void SaveSeen(Dictionary<long, int> seen)
        {
            try
            {
                // Eski talepler birikmesin: en yeni 200 talep
                var trimmed = seen.OrderByDescending(kv => kv.Key).Take(200).ToDictionary(kv => kv.Key, kv => kv.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(SeenPath));
                File.WriteAllText(SeenPath, JsonSerializer.Serialize(trimmed));
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"{SeenPath} yazılamadı: {ex.Message}", true); }
        }
    }
}
