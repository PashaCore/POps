using System;
using System.Collections.Generic;
using System.Diagnostics;
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

#nullable disable

namespace POpsAgent
{
    public sealed class ExamSettings
    {
        [JsonPropertyName("allow")] public List<string> Allow { get; set; } = new List<string>();
        [JsonPropertyName("until")] public long? Until { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("block_apps")] public List<string> BlockApps { get; set; } = new List<string>();
        [JsonPropertyName("since")] public long Since { get; set; }
        // Kurallara giren adresler (sunucu + çözülen izin listesi); değişince kurallar yenilenir
        [JsonPropertyName("addresses")] public List<string> Addresses { get; set; } = new List<string>();
        // Sınavdan önce kapalı olan güvenlik duvarı profilleri (bitince geri kapatılır)
        [JsonPropertyName("previous_disabled_profiles")] public List<string> PreviousDisabledProfiles { get; set; } = new List<string>();
    }

    // Sınav modu (sunucu {"action":"exam_mode","enabled":true,...}): cihaz yalnızca POps sunucusuyla, DNS/DHCP ile ve
    // izin listesiyle (alan adı, IP, CIDR) konuşabilir. Karantinanın yalıtım motoru kendi kural grubuyla ("POps Exam")
    // kullanılır; karantinayla bağımsızdır, ikisi birden sürerse sıkı olan (karantina) geçerlidir. Alan adları 2 dakikada
    // bir ve ağ adresi değişince yeniden çözülür. İsteğe bağlı uygulama listesi oturumlarda kapatılır. "until" geçince
    // mod sunucuya ulaşılamasa da biter. Durum C:\POpsData\secure\exam.json'da (yeniden başlatmada sürer).
    [SupportedOSPlatform("windows")]
    public static class ExamMode
    {
        public const string StateFileName = "exam.json";
        // server_info.features: sunucu exam_mode gönderir ve exam_state okur
        public const string Feature = "exam_mode";
        // Sunucu şeması: allow ve block_apps en çok 50 kayıt (sunucunun mesajı en çok 200 karakter)
        public const int MaxAllow = 50, MaxApps = 50, MaxMessage = 300;
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);
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

        public static string StatePath => SecureStore.PathOf(StateFileName);
        public static bool IsActive => File.Exists(StatePath);

        // Testler: alan adı çözümü
        internal static Func<string, Task<IPAddress[]>> Resolver { get; set; } = host => Dns.GetHostAddressesAsync(host);
        // Testler: engelli uygulamanın kapatılması (testlerde gerçek süreçlere dokunulmaz)
        internal static Action<Process> StopProcess { get; set; } = process => process.Kill();

        public static ExamSettings Load()
        {
            string text = SecureStore.Read(StatePath);
            if (text == null) return null;
            try { return JsonSerializer.Deserialize<ExamSettings>(text, Json); }
            catch (JsonException) { return null; }
        }

        public static bool Expired(ExamSettings settings, DateTimeOffset now) =>
            settings?.Until != null && now.ToUnixTimeSeconds() >= settings.Until.Value;

        // Emir doğrulanır: izin listesi (alan adı, IPv4/IPv6, CIDR; en çok 50), mesaj (en çok 300 karakter, denetim
        // karakterleri atılır), until (gelecekte ya da yok), block_apps (yalnızca "ad.exe"; korunan süreçler düşer).
        public static bool TryParse(JsonElement command, DateTimeOffset now, out ExamSettings settings, out string error)
        {
            settings = null;
            error = null;
            var s = new ExamSettings { Since = now.ToUnixTimeSeconds() };
            if (command.TryGetProperty("allow", out JsonElement allow) && allow.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in allow.EnumerateArray())
                {
                    string value = entry.ValueKind == JsonValueKind.String ? entry.GetString()?.Trim().TrimEnd('.').ToLowerInvariant() : null;
                    if (string.IsNullOrEmpty(value) || !IsValidAllowEntry(value)) { error = $"izin listesinde geçersiz kayıt: {LogText.Safe(value, 60)}"; return false; }
                    if (!s.Allow.Contains(value)) s.Allow.Add(value);
                }
                if (s.Allow.Count > MaxAllow) { error = $"izin listesi en çok {MaxAllow} kayıt olabilir"; return false; }
            }
            if (command.TryGetProperty("until", out JsonElement until) && until.ValueKind == JsonValueKind.Number)
            {
                if (!until.TryGetInt64(out long u) || u <= s.Since) { error = "until geçmişte"; return false; }
                s.Until = u;
            }
            if (command.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                s.Message = LogText.Safe(message.GetString(), MaxMessage);
            if (command.TryGetProperty("block_apps", out JsonElement apps) && apps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement app in apps.EnumerateArray())
                {
                    string name = app.ValueKind == JsonValueKind.String ? app.GetString()?.Trim() : null;
                    if (string.IsNullOrEmpty(name) || !AppRegex.IsMatch(name)) { error = $"uygulama adı geçersiz: {LogText.Safe(name, 60)}"; return false; }
                    if (ProtectedApps.Contains(name)) continue;
                    if (!s.BlockApps.Contains(name, StringComparer.OrdinalIgnoreCase)) s.BlockApps.Add(name.ToLowerInvariant());
                }
                if (s.BlockApps.Count > MaxApps) { error = $"uygulama listesi en çok {MaxApps} kayıt olabilir"; return false; }
            }
            settings = s;
            return true;
        }

        internal static bool IsValidAllowEntry(string value) =>
            TryParseCidr(value, out _) || IPAddress.TryParse(value, out _) || HostRegex.IsMatch(value);

        // "10.1.0.0/24", "2001:db8::/32"
        internal static bool TryParseCidr(string text, out (BigInteger Start, BigInteger End, bool V6) range)
        {
            range = default;
            int slash = text.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || !IPAddress.TryParse(text.AsSpan(0, slash), out IPAddress network)) return false;
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
        internal static async Task<(List<(BigInteger Start, BigInteger End, bool V6)> Ranges, List<string> Addresses)> AllowedAsync(ExamSettings settings, string serverUrl)
        {
            List<IPAddress> server = await NetworkIsolation.ResolveServerAsync(serverUrl);
            if (server.Count == 0) return (null, null);
            var addresses = new List<IPAddress>(server);
            var cidrs = new List<(BigInteger, BigInteger, bool)>();
            foreach (string entry in settings.Allow)
            {
                if (TryParseCidr(entry, out var cidr)) cidrs.Add(cidr);
                else if (IPAddress.TryParse(entry, out IPAddress ip)) addresses.Add(ip);
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
        public static async Task<bool> EnableAsync(ExamSettings settings, string serverUrl)
        {
            await Gate.WaitAsync();
            try
            {
                var (ranges, addresses) = await AllowedAsync(settings, serverUrl);
                if (ranges == null)
                {
                    POpsHelpers.Log("EXAM", $"[GÜVENLİK] Sunucu adresi çözülemedi ({serverUrl}); sınav modu uygulanmadı.", true);
                    return false;
                }
                (int exit, string output) = await NetworkIsolation.ScriptRunner(NetworkIsolation.BuildEnableScript(ranges, NetworkIsolation.ExamRuleGroup));
                if (exit != 0)
                {
                    POpsHelpers.Log("EXAM", $"Sınav modu uygulanamadı (çıkış {exit}): {output}", true);
                    return false;
                }
                ExamSettings previous = Load();
                settings.Addresses = addresses;
                // Zaten sınavdaysa (ayar değişti) ilk başlangıç ve sınav öncesi profiller korunur
                settings.Since = previous?.Since ?? settings.Since;
                settings.PreviousDisabledProfiles = previous?.PreviousDisabledProfiles ?? DisabledProfiles(output);
                Save(settings);
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
        public static async Task<bool> RefreshAsync(string serverUrl, string reason)
        {
            await Gate.WaitAsync();
            try
            {
                ExamSettings settings = Load();
                if (settings == null) return false;
                var (ranges, addresses) = await AllowedAsync(settings, serverUrl);
                if (ranges == null || addresses.SequenceEqual(settings.Addresses ?? new List<string>(), StringComparer.Ordinal)) return false;
                (int exit, string output) = await NetworkIsolation.ScriptRunner(NetworkIsolation.BuildEnableScript(ranges, NetworkIsolation.ExamRuleGroup));
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

        public static async Task<bool> DisableAsync()
        {
            await Gate.WaitAsync();
            try
            {
                ExamSettings settings = Load();
                (int exit, string output) = await NetworkIsolation.ScriptRunner(
                    NetworkIsolation.BuildDisableScript(settings?.PreviousDisabledProfiles ?? new List<string>(), NetworkIsolation.ExamRuleGroup));
                if (exit != 0)
                {
                    POpsHelpers.Log("EXAM", $"Sınav modu kaldırılamadı (çıkış {exit}): {output}", true);
                    return false;
                }
                SecureStore.Delete(StatePath);
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("EXAM", $"Sınav modu kaldırılamadı: {ex.Message}", true);
                return false;
            }
            finally { Gate.Release(); }
        }

        private static void Save(ExamSettings settings) => SecureStore.WriteProtected(StatePath, JsonSerializer.Serialize(settings, Json));

        // Etkinleştirme betiğinin son satırı: önceki profil durumları; kapalı olanların adları
        internal static List<string> DisabledProfiles(string output)
        {
            var disabled = new List<string>();
            string json = output?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('[') || l.StartsWith('{'));
            if (json == null) return disabled;
            try
            {
                JsonNode node = JsonNode.Parse(json.StartsWith('{') ? "[" + json + "]" : json);
                foreach (JsonNode p in node.AsArray())
                    if (p?["Enabled"]?.GetValue<string>() == "False") disabled.Add(p["Name"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException) { }
            return disabled.Where(n => n != null).ToList();
        }

        // Sunucuya {"type":"exam_state","enabled","since","until"}; sunucu şemasında başka alan yok (uygulanamama nedeni
        // yalnızca yerel loga yazılır). since: o anki durumun başladığı an (unix sn): sınavdayken giriş zamanı, sınavda
        // değilken bu çalışmada sınavdan çıkıldıysa çıkış zamanı (leftAt); bilinmiyorsa gönderilmez. until: sınavdayken
        // sınavın bitişi, değilken null.
        public static Dictionary<string, object> StateMessage(long? leftAt)
        {
            ExamSettings s = Load();
            var message = new Dictionary<string, object> { ["type"] = "exam_state", ["enabled"] = s != null };
            if (s != null) message["since"] = s.Since;
            else if (leftAt != null) message["since"] = leftAt.Value;
            message["until"] = s?.Until;
            return message;
        }

        // Kapatılacak süreçler: kullanıcı oturumunda (oturum 0 değil) ve listede
        public static List<int> ProcessesToStop(IEnumerable<(int Pid, string Name, int Session)> processes, ICollection<string> blocked) =>
            processes.Where(p => p.Session != 0 && p.Name != null && blocked.Contains((p.Name + ".exe").ToLowerInvariant()) && !ProtectedApps.Contains(p.Name + ".exe"))
                     .Select(p => p.Pid).ToList();
    }
}
