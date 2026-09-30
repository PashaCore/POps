using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Konsolda oturum açmış kullanıcı (bir logon oturumu: kullanıcı + oturum numarası + açılış zamanı)
    public sealed class SessionSnapshot
    {
        [JsonPropertyName("user")] public string User { get; set; }
        [JsonPropertyName("session_id")] public int SessionId { get; set; }
        [JsonPropertyName("boot_utc")] public DateTime BootUtc { get; set; }

        public static SessionSnapshot Nobody(DateTime bootUtc) => new SessionSnapshot { BootUtc = bootUtc };
    }

    // Sunucu modeli: Backend/pops/models.py AuthEventInput (/api/auth/login, /api/auth/logout)
    public sealed class AuthEventPayload
    {
        [JsonPropertyName("hw_id")] public string HwId { get; set; }
        [JsonPropertyName("hostname")] public string Hostname { get; set; }
        [JsonPropertyName("student_id")] public string StudentId { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }

    public static class SessionEvents
    {
        // Aynı logon oturumu mu? Oturum numaraları her açılışta baştan başlar; açılış zamanı (tick sayacından)
        // birkaç saniye oynayabildiği için 2 dk tolerans.
        public static bool SameLogon(SessionSnapshot a, SessionSnapshot b) =>
            a?.User != null && b?.User != null &&
            string.Equals(a.User, b.User, StringComparison.OrdinalIgnoreCase) &&
            a.SessionId == b.SessionId &&
            Math.Abs((a.BootUtc - b.BootUtc).TotalMinutes) < 2;

        // Son bildirilen durumdan şimdikine geçmek için sırayla bildirilecek olaylar: önce eski kullanıcının
        // çıkışı ("logout"), sonra yenisinin girişi ("login")
        public static List<(string Action, string User)> Diff(SessionSnapshot reported, SessionSnapshot current)
        {
            var events = new List<(string, string)>();
            if (SameLogon(reported, current)) return events;
            if (reported?.User != null) events.Add(("logout", reported.User));
            if (current?.User != null) events.Add(("login", current.User));
            return events;
        }
    }

    // Oturum açma/kapama: servis 15 sn'de bir etkin konsol oturumundaki kullanıcıya bakar ve değişikliği
    // /api/auth/login ve /api/auth/logout ile bildirir (panelde "oturum açan kullanıcı"). Son bildirilen oturum
    // C:\POpsData\session.json'da tutulur: servis yeniden başlayınca aynı oturum için yeniden giriş yazılmaz,
    // makine kapanırken kaçan çıkış da sonraki açılışta bildirilir. Gönderilemeyen olay sonraki turda yeniden denenir.
    [SupportedOSPlatform("windows")]
    public sealed class SessionReporter
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
        // Gönderim başarısızsa (sunucu kapalı, 401...) 15 sn'de bir değil, bu kadar sonra yeniden denenir
        public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

        private readonly string _serverUrl;
        private readonly Func<string> _hwId;
        private readonly string _hostname;
        private readonly Action<string> _error;

        public SessionReporter(string serverUrl, Func<string> hwId, string hostname, Action<string> error = null)
        {
            _serverUrl = serverUrl;
            _hwId = hwId;
            _hostname = hostname;
            _error = error ?? (_ => { });
            Poster = (action, id, body) => AgentHttp.PostJsonAsync(_serverUrl, "/api/auth/" + action, id, body,
                action == "login" ? "Oturum açma bildirimi" : "Oturum kapama bildirimi");
        }

        // Ağ sınırı testlerde sahtesiyle değiştirilir.
        internal Func<string, string, AuthEventPayload, Task<bool>> Poster { get; set; }

        // Konsoldaki kullanıcı değişti (null: kimse yok)
        public event Action<string> UserChanged = delegate { };

        public static string StatePath => Path.Combine(AgentUpdate.DataDir, "session.json");

        public async Task RunAsync(CancellationToken token)
        {
            SessionSnapshot reported = Load();
            string lastSeenUser = null;
            DateTime retryAfterUtc = DateTime.MinValue;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    SessionSnapshot current = ConsoleSession.Current();
                    if (!string.Equals(current.User, lastSeenUser, StringComparison.OrdinalIgnoreCase))
                    {
                        lastSeenUser = current.User;
                        UserChanged(current.User);
                    }
                    if (DateTime.UtcNow >= retryAfterUtc && AgentHttp.EnsureCanReport())
                    {
                        (reported, bool delivered) = await ReportChangesAsync(reported, current);
                        retryAfterUtc = delivered ? DateTime.MinValue : DateTime.UtcNow + RetryDelay;
                    }
                }
                catch (Exception ex)
                {
                    _error(ex.Message);
                    POpsHelpers.Log("AGENT", $"Oturum durumu okunamadı: {ex.Message}", true);
                }
                await Task.Delay(PollInterval, token);
            }
        }

        // Dönen: son bildirilen durum ve bütün olaylar gönderildi mi
        internal async Task<(SessionSnapshot, bool)> ReportChangesAsync(SessionSnapshot reported, SessionSnapshot current)
        {
            foreach (var (action, user) in SessionEvents.Diff(reported, current))
            {
                string hwId = _hwId();
                var body = new AuthEventPayload { HwId = hwId, Hostname = _hostname, StudentId = user };
                if (!await Poster(action, hwId, body))
                    return (reported, false);
                reported = action == "logout" ? SessionSnapshot.Nobody(current.BootUtc) : current;
                Save(reported);
                POpsHelpers.Log("AGENT", action == "login" ? "Oturum açma sunucuya bildirildi." : "Oturum kapama sunucuya bildirildi.");
            }
            return (reported, true);
        }

        internal static SessionSnapshot Load()
        {
            try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(StatePath)) : null; }
            catch { return null; }
        }

        internal static void Save(SessionSnapshot snapshot)
        {
            try
            {
                Directory.CreateDirectory(AgentUpdate.DataDir);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(snapshot));
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"{StatePath} yazılamadı: {ex.Message}", true); }
        }
    }

    // Etkin konsol oturumu (fiziksel ekran ve klavye). RDP oturumları sayılmaz.
    [SupportedOSPlatform("windows")]
    public static class ConsoleSession
    {
        private const uint NoSession = 0xFFFFFFFF;
        private const int WTSUserName = 5;

        public static DateTime BootUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

        public static SessionSnapshot Current()
        {
            DateTime boot = BootUtc();
            uint id = WTSGetActiveConsoleSessionId();
            if (id == NoSession) return SessionSnapshot.Nobody(boot);
            string user = Query(id, WTSUserName);
            return string.IsNullOrWhiteSpace(user) ? SessionSnapshot.Nobody(boot) : new SessionSnapshot { User = user.Trim(), SessionId = (int)id, BootUtc = boot };
        }

        private static string Query(uint sessionId, int infoClass)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out IntPtr buffer, out _)) return null;
            try { return Marshal.PtrToStringUni(buffer); }
            finally { WTSFreeMemory(buffer); }
        }

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);
    }

    // Ön plandaki uygulama: tepsi yalnızca süreç adını gönderir ("ACTIVE_APP:chrome"). KVKK: pencere başlığı (açık
    // belge, site, sohbet adı) kişisel veri içerebildiği için okunmaz ve sunucuya gitmez; eski tepsilerin başlık
    // gönderen "ACTIVE_WINDOW:" mesajı yok sayılır.
    public static class ActiveApp
    {
        public const int MaxLength = 64;

        public static string Sanitize(string name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4).TrimEnd();
            if (name.Length == 0 || name.Length > MaxLength) return null;
            foreach (char c in name)
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '_' || c == '-' || c == '(' || c == ')' || c == '+')) return null;
            return name;
        }
    }
}
