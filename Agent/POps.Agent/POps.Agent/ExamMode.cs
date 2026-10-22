using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using POps.Shared;

#nullable enable

namespace POpsAgent
{
    public sealed class ExamSettings
    {
        [JsonPropertyName("allow")] public List<string> Allow { get; set; } = new List<string>();
        // Sunucunun bitişi (exam_mode.until, sunucunun saatiyle): exam_state.until olarak aynen bildirilir
        [JsonPropertyName("until")] public long? Until { get; set; }
        // Bitiş, bu bilgisayarın saatiyle (unix sn): yalnızca yeniden başlatmadan sonra sürdürmek ve tepsi bandı için; çalışırken
        // bitiş monotonik saatle sayılır (bkz. ExamMode.TimeLeftAsync). null: 0.1.24'ten önceki sürümün dosyası.
        [JsonPropertyName("ends_at")] public long? EndsAt { get; set; }
        // Emir geldiğinde kalan süre (en çok 8 saat), hangi saatle bulunduğu ve emrin geldiği an (monotonik saat): dosyaya yazılmaz
        [JsonIgnore] public TimeSpan Remaining { get; set; }
        [JsonIgnore] internal bool ServerClock { get; set; }
        [JsonIgnore] internal long? ReceivedTimestamp { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("block_apps")] public List<string> BlockApps { get; set; } = new List<string>();
        [JsonPropertyName("since")] public long Since { get; set; }
        // Kurallara giren adresler (sunucu + çözülen izin listesi); değişince kurallar yenilenir
        [JsonPropertyName("addresses")] public List<string> Addresses { get; set; } = new List<string>();
        // Sınavdan önce kapalı olan güvenlik duvarı profilleri (bitince ve MSI kaldırılırken geri kapatılır; alan adı ortak)
        [JsonPropertyName(FirewallRules.ExamProfilesKey)] public List<string> PreviousDisabledProfiles { get; set; } = new List<string>();
    }

    // Sınav modu (sunucu {"action":"exam_mode","enabled":true,...}): cihaz yalnızca POps sunucusuyla, DNS/DHCP ile ve
    // izin listesiyle (alan adı, IP, CIDR) konuşabilir. Karantinanın yalıtım motoru kendi kural grubuyla ("POps Exam")
    // kullanılır; karantinayla bağımsızdır, ikisi birden sürerse sıkı olan (karantina) geçerlidir. Alan adları 2 dakikada
    // bir ve ağ adresi değişince yeniden çözülür. İsteğe bağlı uygulama listesi oturumlarda kapatılır. Süre dolunca mod
    // sunucuya ulaşılamasa da biter: kalan süre until - now (sunucunun saatiyle; now yoksa until - bu bilgisayarın saati),
    // en çok 8 saat, monotonik saatle sayılır; bilgisayarın saati ileri ya da geri olsa da sınav uzamaz, kısalmaz. Durum
    // C:\POpsData\secure\exam.json'da (yeniden başlatmada sürer). Worker'ın örneği sınav işleyicisindedir (bkz. ExamHandler).
    [SupportedOSPlatform("windows")]
    public sealed class ExamMode
    {
        public const string StateFileName = FirewallRules.ExamStateFile;
        // server_info.features: sunucu exam_mode gönderir ve exam_state okur
        public const string Feature = "exam_mode";
        // Sunucu şeması: allow ve block_apps en çok 50 kayıt (sunucunun mesajı en çok 200 karakter)
        public const int MaxAllow = 50, MaxApps = 50, MaxMessage = 300;
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);
        // Sınav en çok 8 saat sürer (until yoksa da, daha ilerideyse de; sunucu da en çok 8 saat sonrasını gönderir)
        public const long MaxSeconds = 8 * 60 * 60;
        public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(MaxSeconds);
        // Kayıtlı bitiş (ends_at) bilgisayarın saatine göre bundan fazla kayınca yeniden yazılır (saat ayarlandı ya da eşitlendi)
        internal static readonly TimeSpan EndsAtDrift = TimeSpan.FromMinutes(1);
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        private static readonly Regex HostRegex = new Regex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AppRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,59}\.exe$", RegexOptions.Compiled);

        // Kapatılması oturumu ya da sistemi bozacak, POps'u durduracak süreçler: listede olsalar da kapatılmaz
        private static readonly HashSet<string> ProtectedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "svchost.exe", "csrss.exe", "winlogon.exe", "lsass.exe", "services.exe", "smss.exe", "wininit.exe",
            "dwm.exe", "fontdrvhost.exe", "logonui.exe", "userinit.exe", "sihost.exe", "ctfmon.exe", "taskhostw.exe",
            "runtimebroker.exe", "conhost.exe", "system", "popsagent.exe", "popstray.exe", "popswatchdog.exe", "popsupdater.exe",
            "msiexec.exe",
        };

        // statePath: durum dosyası (<güvenli depo>\exam.json); scripts: güvenlik duvarı betikleri (karantinanın motoru, kendi
        // kural grubuyla; bkz. MachineSeams.FirewallScripts); resolver: izin listesindeki alan adlarının çözümü; stopProcess:
        // engelli uygulamanın kapatılması. Servis: gerçekleri (MachineSeams.Production); testlerde makineye dokunmayan sahteler.
        // clock: bilgisayarın saati (GetUtcNow) ve bitişi sayan monotonik saat (GetTimestamp); servis: AgentClock (bkz.
        // AgentContext.Clock), testlerde ikisi ayrı ayrı ilerleyen sahte.
        public ExamMode(string statePath, Func<string, Task<(int Exit, string Output)>> scripts, Func<string, Task<IPAddress[]>> resolver,
            Action<Process> stopProcess, TimeProvider clock)
        {
            StatePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
            Scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            StopProcess = stopProcess ?? throw new ArgumentNullException(nameof(stopProcess));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public string StatePath { get; }
        public bool IsActive => IsActiveAt(StatePath);
        internal Func<string, Task<(int Exit, string Output)>> Scripts { get; }
        internal Func<string, Task<IPAddress[]>> Resolver { get; }
        internal Action<Process> StopProcess { get; }
        internal TimeProvider Clock { get; }
        // Bu çalışmada sınavın bitişi, monotonik saatle (Clock.GetTimestamp); null: kurulmadı (yeni süreç: ilk TimeLeftAsync
        // durum dosyasından kurar). Yalnızca Gate altında okunur ve yazılır.
        private long? _endTimestamp;

        public static bool IsActiveAt(string statePath) => File.Exists(statePath);

        public ExamSettings? Load()
        {
            string? text = SecureStore.Read(StatePath);
            if (text == null) return null;
            try { return JsonSerializer.Deserialize<ExamSettings>(text, Json); }
            catch (JsonException) { return null; }
        }

        // Emir şimdi geldi: bilgisayarın saatiyle doğrulanır (bkz. TryParse); kalan süre bu andan monotonik saatle sayılır
        public bool TryAccept(JsonElement command, [NotNullWhen(true)] out ExamSettings? settings, [NotNullWhen(false)] out string? error)
        {
            long received = Clock.GetTimestamp();
            if (!TryParse(command, Clock.GetUtcNow(), out settings, out error)) return false;
            settings.ReceivedTimestamp = received;
            return true;
        }

        // Emir doğrulanır: izin listesi (alan adı, IPv4/IPv6, CIDR; en çok 50), mesaj (en çok 300 karakter, denetim
        // karakterleri atılır), until (gelecekte ya da yok), block_apps (yalnızca "ad.exe"; korunan süreçler düşer).
        // now: bu bilgisayarın saati. Kalan süre until - now (emirdeki now, sunucunun saati; 0.1.24 sunucuları gönderir), yoksa
        // until - bu bilgisayarın saati (eski sunucu); sıfır ya da geçmişse emir reddedilir. En çok 8 saat; until yoksa ya da
        // null ise 8 saat.
        public static bool TryParse(JsonElement command, DateTimeOffset now, [NotNullWhen(true)] out ExamSettings? settings, [NotNullWhen(false)] out string? error)
        {
            settings = null;
            error = null;
            var s = new ExamSettings { Since = now.ToUnixTimeSeconds(), Remaining = MaxDuration };
            if (command.TryGetProperty("allow", out JsonElement allow) && allow.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in allow.EnumerateArray())
                {
                    string? value = entry.ValueKind == JsonValueKind.String ? entry.GetString()?.Trim().TrimEnd('.').ToLowerInvariant() : null;
                    if (string.IsNullOrEmpty(value) || !IsValidAllowEntry(value)) { error = $"izin listesinde geçersiz kayıt: {LogText.Safe(value, 60)}"; return false; }
                    if (!s.Allow.Contains(value)) s.Allow.Add(value);
                }
                if (s.Allow.Count > MaxAllow) { error = $"izin listesi en çok {MaxAllow} kayıt olabilir"; return false; }
            }
            if (command.TryGetProperty("until", out JsonElement until) && until.ValueKind == JsonValueKind.Number)
            {
                long reference = s.Since;
                if (command.TryGetProperty("now", out JsonElement sent) && sent.ValueKind == JsonValueKind.Number && sent.TryGetInt64(out long serverNow))
                {
                    reference = serverNow;
                    s.ServerClock = true;
                }
                TimeSpan? left = until.TryGetInt64(out long u) ? TimeLeft(u, reference) : null;
                if (left == null) { error = s.ServerClock ? "until geçmişte (sunucunun saatiyle)" : "until geçmişte"; return false; }
                s.Until = u;
                s.Remaining = left.Value;
            }
            s.EndsAt = s.Since + (long)s.Remaining.TotalSeconds;
            if (command.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                s.Message = LogText.Safe(message.GetString(), MaxMessage);
            if (command.TryGetProperty("block_apps", out JsonElement apps) && apps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement app in apps.EnumerateArray())
                {
                    string? name = app.ValueKind == JsonValueKind.String ? app.GetString()?.Trim() : null;
                    if (string.IsNullOrEmpty(name) || !AppRegex.IsMatch(name)) { error = $"uygulama adı geçersiz: {LogText.Safe(name, 60)}"; return false; }
                    if (ProtectedApps.Contains(name)) continue;
                    if (!s.BlockApps.Contains(name, StringComparer.OrdinalIgnoreCase)) s.BlockApps.Add(name.ToLowerInvariant());
                }
                if (s.BlockApps.Count > MaxApps) { error = $"uygulama listesi en çok {MaxApps} kayıt olabilir"; return false; }
            }
            settings = s;
            return true;
        }

        // until - reference (unix sn) süre olarak, en çok 8 saat; null: sıfır ya da geçmişte. Fark long'a sığmasa da taşmaz.
        internal static TimeSpan? TimeLeft(long until, long reference)
        {
            if (until <= reference) return null;
            ulong seconds = unchecked((ulong)until - (ulong)reference);
            return seconds >= MaxSeconds ? MaxDuration : TimeSpan.FromSeconds((long)seconds);
        }

        internal static bool IsValidAllowEntry(string value) =>
            TryParseCidr(value, out _) || IPAddress.TryParse(value, out _) || HostRegex.IsMatch(value);

        // "10.1.0.0/24", "2001:db8::/32"
        internal static bool TryParseCidr(string text, out (BigInteger Start, BigInteger End, bool V6) range)
        {
            range = default;
            int slash = text.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || !IPAddress.TryParse(text.AsSpan(0, slash), out IPAddress? network)) return false;
            bool v6 = network.AddressFamily == AddressFamily.InterNetworkV6;
            if (!int.TryParse(text.AsSpan(slash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int prefix)
                || prefix < (v6 ? 16 : 8) || prefix > (v6 ? 128 : 32)) return false;   // çok geniş aralık sınavı anlamsızlaştırır
            var (start, _, _) = NetworkIsolation.Cidr(network.ToString(), prefix);
            BigInteger size = BigInteger.One << ((v6 ? 128 : 32) - prefix);
            start -= start % size;
            range = (start, start + size - 1, v6);
            return true;
        }

        // Kurallara girecek adresler: sunucu (zorunlu), DNS/DHCP, izin listesindeki IP'ler ve çözülen alan adları
        internal async Task<(List<(BigInteger Start, BigInteger End, bool V6)>? Ranges, List<string>? Addresses)> AllowedAsync(ExamSettings settings, string serverUrl)
        {
            List<IPAddress> server = await NetworkIsolation.ResolveServerAsync(serverUrl);
            if (server.Count == 0) return (null, null);
            var addresses = new List<IPAddress>(server);
            var cidrs = new List<(BigInteger, BigInteger, bool)>();
            foreach (string entry in settings.Allow)
            {
                if (TryParseCidr(entry, out var cidr)) cidrs.Add(cidr);
                else if (IPAddress.TryParse(entry, out IPAddress? ip)) addresses.Add(ip);
                else
                {
                    try { addresses.AddRange(await Resolver(entry)); }
                    catch (SocketException) { POpsHelpers.Log("EXAM", $"Sınav izin listesi: {entry} çözülemedi; şimdilik kapalı kalır, yeniden denenecek.", true); }
                }
            }
            List<(BigInteger, BigInteger, bool)> ranges = NetworkIsolation.AllowedRanges(addresses.Concat(NetworkIsolation.LocalInfrastructure()));
            ranges.AddRange(cidrs);
            List<string> recorded = NetworkIsolation.NormalizeAddresses(addresses).Concat(settings.Allow.Where(e => e.Contains('/', StringComparison.Ordinal))).OrderBy(a => a, StringComparer.Ordinal).ToList();
            return (ranges, recorded);
        }

        // Kurallar kurulur ve durum yazılır. Sunucu adresi çözülemezse uygulanmaz (cihaz sunucudan kopardı).
        public async Task<bool> EnableAsync(ExamSettings settings, string serverUrl)
        {
            await Gate.WaitAsync();
            try
            {
                var (ranges, addresses) = await AllowedAsync(settings, serverUrl);
                if (ranges == null || addresses == null)
                {
                    POpsHelpers.Log("EXAM", $"[GÜVENLİK] Sunucu adresi çözülemedi ({serverUrl}); sınav modu uygulanmadı.", true);
                    return false;
                }
                (int exit, string output) = await Scripts(NetworkIsolation.BuildEnableScript(ranges, NetworkIsolation.ExamRuleGroup));
                if (exit != 0)
                {
                    POpsHelpers.Log("EXAM", $"Sınav modu uygulanamadı (çıkış {exit}): {output}", true);
                    return false;
                }
                ExamSettings? previous = Load();
                settings.Addresses = addresses;
                // Zaten sınavdaysa (ayar değişti) ilk başlangıç ve sınav öncesi profiller korunur; bitiş yeni emrindir (sunucu
                // her bağlantıda süren sınavı yeniden gönderir: yeniden başlatmadan sonra kayıtlı bitişin yerine geçer)
                settings.Since = previous?.Since ?? settings.Since;
                settings.PreviousDisabledProfiles = previous?.PreviousDisabledProfiles ?? DisabledProfiles(output);
                Save(settings);
                _endTimestamp = (settings.ReceivedTimestamp ?? Clock.GetTimestamp()) + Timestamps(settings.Remaining);
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("EXAM", $"Sınav modu uygulanamadı: {ex.Message}", true);
                return false;
            }
            finally { Gate.Release(); }
        }

        // Alan adları yeniden çözülür; adresler değiştiyse kurallar yenilenir. Dönen: kurallar yenilendi mi.
        public async Task<bool> RefreshAsync(string serverUrl, string reason)
        {
            await Gate.WaitAsync();
            try
            {
                ExamSettings? settings = Load();
                if (settings == null) return false;
                var (ranges, addresses) = await AllowedAsync(settings, serverUrl);
                if (ranges == null || addresses == null || addresses.SequenceEqual(settings.Addresses ?? new List<string>(), StringComparer.Ordinal)) return false;
                (int exit, string output) = await Scripts(NetworkIsolation.BuildEnableScript(ranges, NetworkIsolation.ExamRuleGroup));
                if (exit != 0)
                {
                    POpsHelpers.Log("EXAM", $"Sınav izin listesi yenilenemedi ({reason}, çıkış {exit}): {output}", true);
                    return false;
                }
                settings.Addresses = addresses;
                Save(settings);
                POpsHelpers.Log("EXAM", $"Sınav izin listesi yenilendi ({reason}): {addresses.Count} adres.");
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("EXAM", $"Sınav izin listesi yenilenemedi ({reason}): {ex.Message}", true);
                return false;
            }
            finally { Gate.Release(); }
        }

        public async Task<bool> DisableAsync()
        {
            await Gate.WaitAsync();
            try
            {
                ExamSettings? settings = Load();
                (int exit, string output) = await Scripts(
                    NetworkIsolation.BuildDisableScript(settings?.PreviousDisabledProfiles ?? new List<string>(), NetworkIsolation.ExamRuleGroup));
                if (exit != 0)
                {
                    POpsHelpers.Log("EXAM", $"Sınav modu kaldırılamadı (çıkış {exit}): {output}", true);
                    return false;
                }
                SecureStore.Delete(StatePath);
                _endTimestamp = null;
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("EXAM", $"Sınav modu kaldırılamadı: {ex.Message}", true);
                return false;
            }
            finally { Gate.Release(); }
        }

        // Bitişe kalan süre, bu çalışmanın monotonik saatiyle: bilgisayarın saati ileri ya da geri alınsa da değişmez. null: sınav
        // yok. Yeni süreçte (yeniden başlatma, servis yeniden başladı) ilk çağrı bitişi durum dosyasından kurar: kayıtlı bitiş
        // (ends_at, bilgisayarın saatiyle), en çok bilgisayarın saatinden 8 saat sonra. Kayıtlı bitiş bilgisayarın saatine göre
        // kaydıysa (saat ayarlandı ya da eşitlendi, 8 saatle kısaltıldı) yeniden yazılır: sonraki yeniden başlatma aynı bitişi
        // bulur. EndsAtMoved: bu çağrıda yeniden yazıldı (tepsi bandı yeni bitişi göstersin).
        public async Task<(TimeSpan Left, bool EndsAtMoved)?> TimeLeftAsync()
        {
            await Gate.WaitAsync();
            try
            {
                ExamSettings? settings = Load();
                if (settings == null) return null;
                long ticks = Clock.GetTimestamp();
                long wall = Clock.GetUtcNow().ToUnixTimeSeconds();
                if (_endTimestamp == null)
                {
                    // 0.1.24'ten önceki dosya: sunucunun bitişi, o da yoksa girişten 8 saat sonra
                    long saved = settings.EndsAt ?? settings.Until ?? settings.Since + MaxSeconds;
                    TimeSpan resumed = TimeLeft(saved, wall) ?? TimeSpan.Zero;
                    _endTimestamp = ticks + Timestamps(resumed);
                    POpsHelpers.Log("EXAM", $"Sınav modu sürüyor: kayıtlı bitiş {LocalTime(saved)}, kalan {Minutes(resumed)} dk" +
                        (resumed == MaxDuration && saved - wall > MaxSeconds ? " (bilgisayarın saatinden 8 saatten fazla ileride; 8 saatle sınırlandı)." : "."));
                }
                TimeSpan left = Clock.GetElapsedTime(ticks, _endTimestamp.Value);
                long endsAt = wall + (long)Math.Ceiling(left.TotalSeconds);
                bool moved = left > TimeSpan.Zero && (settings.EndsAt == null || Math.Abs(endsAt - settings.EndsAt.Value) > EndsAtDrift.TotalSeconds);
                if (moved)
                {
                    settings.EndsAt = endsAt;
                    Save(settings);
                }
                return (left, moved);
            }
            finally { Gate.Release(); }
        }

        // Süre, monotonik saatin biriminde (Clock.TimestampFrequency)
        private long Timestamps(TimeSpan duration) => (long)(duration.TotalSeconds * Clock.TimestampFrequency);

        // Loglar için yerel saat ("HH:mm"); eski bir dosyadaki aralık dışı değer "?" olur
        internal static string LocalTime(long unixSeconds)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { return "?"; }
        }

        internal static long Minutes(TimeSpan duration) => (long)Math.Ceiling(duration.TotalMinutes);

        private void Save(ExamSettings settings) => SecureStore.WriteProtected(StatePath, JsonSerializer.Serialize(settings, Json));

        // Etkinleştirme betiğinin son satırı: önceki profil durumları; kapalı olanların adları
        internal static List<string> DisabledProfiles(string? output)
        {
            var disabled = new List<string?>();
            string? json = output?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('[') || l.StartsWith('{'));
            if (json == null) return new List<string>();
            try
            {
                if (JsonNode.Parse(json.StartsWith('{') ? "[" + json + "]" : json) is JsonArray profiles)
                    foreach (JsonNode? p in profiles)
                        if (p?["Enabled"]?.GetValue<string>() == "False") disabled.Add(p["Name"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { }
            return disabled.OfType<string>().ToList();
        }

        // Sunucuya {"type":"exam_state","enabled","since","until"}; sunucu şemasında başka alan yok (uygulanamama nedeni
        // yalnızca yerel loga yazılır). since: o anki durumun başladığı an (unix sn): sınavdayken giriş zamanı, sınavda
        // değilken bu çalışmada sınavdan çıkıldıysa çıkış zamanı (leftAt); bilinmiyorsa gönderilmez. until: sınavdayken
        // sınavın bitişi, değilken null.
        public Dictionary<string, object?> StateMessage(long? leftAt)
        {
            ExamSettings? s = Load();
            var message = new Dictionary<string, object?> { ["type"] = "exam_state", ["enabled"] = s != null };
            if (s != null) message["since"] = s.Since;
            else if (leftAt != null) message["since"] = leftAt.Value;
            message["until"] = s?.Until;
            return message;
        }

        // Kapatılacak süreçler: kullanıcı oturumunda (oturum 0 değil) ve listede
        public static List<int> ProcessesToStop(IEnumerable<(int Pid, string? Name, int Session)> processes, ICollection<string> blocked) =>
            processes.Where(p => p.Session != 0 && p.Name != null && blocked.Contains((p.Name + ".exe").ToLowerInvariant()) && !ProtectedApps.Contains(p.Name + ".exe"))
                     .Select(p => p.Pid).ToList();
    }
}
