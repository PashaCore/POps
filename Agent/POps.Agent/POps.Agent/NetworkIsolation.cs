using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Ağ karantinası (lockdown / DNS eşiği): cihaz yalnızca POps sunucusuyla (ve adres çözümü için DNS/DHCP ile)
    // konuşabilir; offline bypass ya da sunucunun "unlock" emri kaldırır.
    //
    // Eski uygulama hiç çalışmıyordu: çok satırlı PowerShell komutu bir .bat dosyasına yazılıyordu (her satır ayrı
    // bir cmd komutu oldu), -RemoteAddress'e IP yerine sunucunun ana bilgisayar adı veriliyordu ve Windows
    // Güvenlik Duvarı'nda "engelle" kuralı "izin ver" kuralını her zaman geçtiği için sunucuya izin kuralı da işe
    // yaramazdı. Şimdi:
    //  * betik -EncodedCommand ile tek parça çalıştırılır;
    //  * sunucu adı IP'lere çözülür;
    //  * izin kuralı yerine, izinli adresler DIŞINDAKİ tüm IPv4/IPv6 aralıklarını engelleyen iki block kuralı
    //    (giden/gelen) eklenir: block her allow kuralını geçtiği için başka uygulamaların izin kuralları da
    //    karantinayı delemez;
    //  * güvenlik duvarı profilleri açılır; önceki durum C:\POpsData\secure\isolation.json'a yazılır ve
    //    kaldırmada geri yüklenir.
    //  * Karantina sürerken sunucunun adresi değişebilir (DNS kaydı): 5 dakikada bir ve art arda 3 bağlantı hatasından
    //    sonra sunucu adı yeniden çözülür (DNS karantinada açık); küme değiştiyse ve boş değilse kurallar yeni
    //    adreslerle yeniden kurulur. Eskiden adresler yalnızca karantina anında çözülüyordu: sunucunun IP'si değişince
    //    cihaz kalıcı koparıyordu, yalnızca bypass kurtarıyordu. isolation.json'daki "önceki profiller" İLK
    //    karantinadaki hâliyle kalır (kaldırmada profiller doğru geri yüklensin); yalnızca server_addresses güncellenir.
    [SupportedOSPlatform("windows")]
    public sealed class NetworkIsolation
    {
        // Kural grupları, profil adları ve durum dosyası tek yerde (POps.Shared.FirewallRules): MSI kaldırılırken aynı adlarla
        // silinir.
        public const string RuleGroup = FirewallRules.IsolationGroup;
        // Sınav modu aynı motoru kendi kural grubuyla kullanır (bkz. ExamMode). Bir grup kalkarken öteki sürüyorsa
        // güvenlik duvarı profilleri kapatılmaz: kapatmak ötekinin kurallarını da etkisiz bırakırdı.
        public const string ExamRuleGroup = FirewallRules.ExamGroup;
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
        public const int RefreshAfterFailures = 3;

        public enum RefreshResult { NotIsolated, Unchanged, Updated, ResolveFailed, Failed }

        // Durum dosyası güvenli depoda (AgentPaths.SecureFile(StateFileName)). Worker kurar; QuarantineControl ve eş önbelleği
        // yolu AgentPaths'ten alır.
        public const string StateFileName = FirewallRules.IsolationStateFile;

        // statePath: durum dosyası (<güvenli depo>\isolation.json); scripts: güvenlik duvarı betiklerini çalıştıran (servis:
        // PowerShell, bkz. MachineSeams.FirewallScripts; testlerde güvenlik duvarına dokunmayan sahte)
        public NetworkIsolation(string statePath, Func<string, Task<(int Exit, string Output)>> scripts)
        {
            StatePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
            Scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
        }

        public string StatePath { get; }
        public bool IsActive => IsActiveAt(StatePath);
        internal Func<string, Task<(int Exit, string Output)>> Scripts { get; }
        internal List<string> ReadPreviouslyDisabledProfiles() => ReadPreviouslyDisabledProfiles(StatePath);

        public static bool IsActiveAt(string statePath) => File.Exists(statePath);

        public async Task<bool> EnableAsync(string serverUrl)
        {
            string statePath = StatePath;
            await Gate.WaitAsync();
            try
            {
                List<IPAddress> server = await ResolveServerAsync(serverUrl);
                if (server.Count == 0)
                {
                    // Sunucu adresi bilinmeden karantina, cihazı sunucudan da koparırdı (yalnızca bypass ile açılır)
                    POpsHelpers.Log("ISOLATION", $"[GÜVENLİK] Sunucu adresi çözülemedi ({serverUrl}); karantina uygulanmadı.", true);
                    return false;
                }

                List<(BigInteger Start, BigInteger End, bool V6)> allowed = AllowedRanges(server.Concat(LocalInfrastructure()));
                (int exit, string output) = await Scripts(BuildEnableScript(allowed));
                if (exit != 0)
                {
                    POpsHelpers.Log("ISOLATION", $"Karantina uygulanamadı (çıkış {exit}): {output}", true);
                    return false;
                }
                SavePreviousProfiles(statePath, output);
                SaveServerAddresses(statePath, server);
                POpsHelpers.Log("ISOLATION", $"AĞ KARANTİNASI AKTİF: yalnızca sunucu ({string.Join(", ", server)}), DNS ve DHCP erişilebilir.");
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("ISOLATION", $"Karantina uygulanamadı: {ex.Message}", true);
                return false;
            }
            finally { Gate.Release(); }
        }

        public async Task<bool> DisableAsync()
        {
            string statePath = StatePath;
            await Gate.WaitAsync();
            try
            {
                (int exit, string output) = await Scripts(BuildDisableScript(ReadPreviouslyDisabledProfiles(statePath)));
                if (exit != 0)
                {
                    POpsHelpers.Log("ISOLATION", $"Karantina kaldırılamadı (çıkış {exit}): {output}", true);
                    return false;
                }
                SecureStore.Delete(statePath);
                POpsHelpers.Log("ISOLATION", "AĞ KARANTİNASI KALDIRILDI.");
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("ISOLATION", $"Karantina kaldırılamadı: {ex.Message}", true);
                return false;
            }
            finally { Gate.Release(); }
        }

        // Karantina sürüyorsa sunucu adı yeniden çözülür; adres kümesi değiştiyse kurallar yeni adreslerle kurulur.
        // Çözüm boşsa ya da hata verirse mevcut kurallara dokunulmaz.
        public async Task<RefreshResult> RefreshServerAddressesAsync(string serverUrl, string reason)
        {
            string statePath = StatePath;
            await Gate.WaitAsync();
            try
            {
                if (!File.Exists(statePath)) return RefreshResult.NotIsolated;
                List<IPAddress> server = await ResolveServerAsync(serverUrl);
                if (server.Count == 0)
                {
                    POpsHelpers.Log("ISOLATION", $"Karantina: sunucu adı çözülemedi ({reason}); mevcut kurallara dokunulmadı.", true);
                    return RefreshResult.ResolveFailed;
                }
                List<string> recorded = ReadServerAddresses(SecureStore.Read(statePath));
                if (recorded != null && SameAddresses(server, recorded)) return RefreshResult.Unchanged;

                List<(BigInteger Start, BigInteger End, bool V6)> allowed = AllowedRanges(server.Concat(LocalInfrastructure()));
                (int exit, string output) = await Scripts(BuildEnableScript(allowed));
                if (exit != 0)
                {
                    POpsHelpers.Log("ISOLATION", $"Karantina izin listesi yenilenemedi (çıkış {exit}): {output}", true);
                    return RefreshResult.Failed;
                }
                // Önceki profil durumu ilk karantinadaki hâliyle kalır; yalnızca sunucu adresleri güncellenir
                SaveServerAddresses(statePath, server);
                List<string> now = NormalizeAddresses(server);
                POpsHelpers.Log("ISOLATION", $"Karantina izin listesi yenilendi ({reason}): sunucu {(recorded == null ? "(bilinmiyor)" : string.Join(", ", recorded))} -> {string.Join(", ", now)}.");
                LocalAudit.Write(LocalAudit.QuarantineAllowListRefreshed(recorded, now, reason));
                return RefreshResult.Updated;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("ISOLATION", $"Karantina izin listesi yenilenemedi ({reason}): {ex.Message}", true);
                return RefreshResult.Failed;
            }
            finally { Gate.Release(); }
        }

        private static void SaveServerAddresses(string statePath, IEnumerable<IPAddress> server)
        {
            try { SecureStore.WriteProtected(statePath, MergeServerAddresses(SecureStore.Read(statePath), server)); }
            catch (Exception ex) { POpsHelpers.Log("ISOLATION", $"Sunucu adresleri isolation.json'a yazılamadı: {ex.Message}", true); }
        }

        // isolation.json'a server_addresses yazılır; diğer alanlar (previous_profiles, since) olduğu gibi kalır
        internal static string MergeServerAddresses(string stateJson, IEnumerable<IPAddress> server)
        {
            JsonObject state = null;
            try { state = string.IsNullOrWhiteSpace(stateJson) ? null : JsonNode.Parse(stateJson) as JsonObject; }
            catch (JsonException) { }
            state ??= new JsonObject();
            var addresses = new JsonArray();
            foreach (string a in NormalizeAddresses(server)) addresses.Add(a);
            state["server_addresses"] = addresses;
            return state.ToJsonString();
        }

        // Kayıtlı sunucu adresleri; alan yoksa (eski sürümün karantinası) ya da okunamazsa null
        internal static List<string> ReadServerAddresses(string stateJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(stateJson)) return null;
                using JsonDocument doc = JsonDocument.Parse(stateJson);
                if (!doc.RootElement.TryGetProperty("server_addresses", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return null;
                return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToList();
            }
            catch (JsonException) { return null; }
        }

        // Karşılaştırma sıradan ve yazımdan bağımsız: IPv4'e eşlenmiş IPv6 ve kapsam kimliği ayıklanır
        internal static List<string> NormalizeAddresses(IEnumerable<IPAddress> addresses) =>
            addresses.Where(a => a != null).Select(Normalize).Select(a => a.ToString()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

        internal static bool SameAddresses(IEnumerable<IPAddress> resolved, IEnumerable<string> recorded)
        {
            var parsed = new List<IPAddress>();
            foreach (string s in recorded ?? Enumerable.Empty<string>())
                if (IPAddress.TryParse(s, out IPAddress a)) parsed.Add(a);
            return NormalizeAddresses(resolved).SequenceEqual(NormalizeAddresses(parsed));
        }

        private static IPAddress Normalize(IPAddress address)
        {
            IPAddress a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0 ? new IPAddress(a.GetAddressBytes()) : a;
        }

        // ------------------------------------------------------------------------------------------
        // Adresler
        // ------------------------------------------------------------------------------------------
        internal static async Task<List<IPAddress>> ResolveServerAsync(string serverUrl)
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri uri)) return new List<IPAddress>();
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress literal)) return new List<IPAddress> { literal };
            try { return (await Dns.GetHostAddressesAsync(uri.DnsSafeHost)).Distinct().ToList(); }
            catch (SocketException) { return new List<IPAddress>(); }
        }

        // Sunucuya yeniden bağlanabilmek ve IP adresini koruyabilmek için gerekenler: DNS ve DHCP sunucuları,
        // yerel yayın, IPv6 bağlantı-yerel (komşu keşfi) ve çoklu yayın adresleri.
        internal static IEnumerable<IPAddress> LocalInfrastructure()
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
                foreach (IPAddress dns in props.DnsAddresses) yield return dns;
                foreach (IPAddress dhcp in props.DhcpServerAddresses) yield return dhcp;
            }
            yield return IPAddress.Broadcast;
        }

        internal static List<(BigInteger Start, BigInteger End, bool V6)> AllowedRanges(IEnumerable<IPAddress> addresses)
        {
            var ranges = new List<(BigInteger, BigInteger, bool)>();
            foreach (IPAddress address in addresses.Where(a => a != null).Distinct())
            {
                IPAddress a = Normalize(address);
                BigInteger v = ToBig(a);
                ranges.Add((v, v, a.AddressFamily == AddressFamily.InterNetworkV6));
            }
            ranges.Add(Cidr("127.0.0.0", 8));
            ranges.Add(Cidr("::1", 128));
            ranges.Add(Cidr("fe80::", 10));
            ranges.Add(Cidr("ff00::", 8));
            return ranges;
        }

        // İzinli aralıklar dışında kalan tüm aralıklar ("a-b" ya da tek adres), IPv4 ve IPv6 ayrı ayrı.
        internal static List<string> BlockedRanges(IEnumerable<(BigInteger Start, BigInteger End, bool V6)> allowed)
        {
            var result = new List<string>();
            foreach (bool v6 in new[] { false, true })
            {
                BigInteger max = (BigInteger.One << (v6 ? 128 : 32)) - 1;
                BigInteger cursor = 0;
                foreach (var r in allowed.Where(r => r.V6 == v6).OrderBy(r => r.Start))
                {
                    if (r.Start > cursor) result.Add(Format(cursor, r.Start - 1, v6));
                    if (r.End + 1 > cursor) cursor = r.End + 1;
                }
                if (cursor <= max) result.Add(Format(cursor, max, v6));
            }
            return result;
        }

        internal static (BigInteger, BigInteger, bool) Cidr(string network, int prefix)
        {
            IPAddress a = IPAddress.Parse(network);
            bool v6 = a.AddressFamily == AddressFamily.InterNetworkV6;
            int bits = v6 ? 128 : 32;
            BigInteger start = ToBig(a);
            return (start, start + (BigInteger.One << (bits - prefix)) - 1, v6);
        }

        private static BigInteger ToBig(IPAddress a) => new BigInteger(a.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

        private static string Format(BigInteger start, BigInteger end, bool v6)
        {
            string s = FromBig(start, v6).ToString(), e = FromBig(end, v6).ToString();
            return start == end ? s : $"{s}-{e}";
        }

        private static IPAddress FromBig(BigInteger value, bool v6)
        {
            int size = v6 ? 16 : 4;
            byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] bytes = new byte[size];
            Array.Copy(raw, 0, bytes, size - raw.Length, raw.Length);
            return new IPAddress(bytes);
        }

        // ------------------------------------------------------------------------------------------
        // Betikler (yalnızca sabit metin + IP aralıkları; dışarıdan gelen hiçbir değer betiğe girmez)
        // ------------------------------------------------------------------------------------------
        internal static string BuildEnableScript(IEnumerable<(BigInteger Start, BigInteger End, bool V6)> allowed, string group = RuleGroup)
        {
            // Kaldırma yalnızca FirewallRules.Groups'taki grupları siler: başka bir grupta kural kurulmaz
            if (!FirewallRules.IsGroup(group)) throw new ArgumentException($"Güvenlik duvarı grubu POps.Shared.FirewallRules.Groups'ta yok: {group}", nameof(group));
            string blocked = string.Join(",", BlockedRanges(allowed).Select(r => $"'{r}'"));
            return $@"$ErrorActionPreference = 'Stop'
$group = '{group}'
$previous = @(Get-NetFirewallProfile | ForEach-Object {{ [pscustomobject]@{{ Name = [string]$_.Name; Enabled = [string]$_.Enabled }} }})
$old = @(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue)
Get-NetFirewallRule -DisplayName '{FirewallRules.LegacyIsolationRules}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$blocked = @({blocked})
New-NetFirewallRule -Group $group -DisplayName '{group} - Outbound' -Direction Outbound -Action Block -Profile Any -RemoteAddress $blocked | Out-Null
New-NetFirewallRule -Group $group -DisplayName '{group} - Inbound' -Direction Inbound -Action Block -Profile Any -RemoteAddress $blocked | Out-Null
# Eski kurallar yeniler kurulduktan SONRA kalkar: yenilemede cihaz bir an bile korumasız kalmaz
if ($old.Count -gt 0) {{ $old | Remove-NetFirewallRule }}
Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True
ConvertTo-Json -Compress -InputObject $previous
";
        }

        internal static string BuildDisableScript(IEnumerable<string> profilesToDisable, string group = RuleGroup)
        {
            string other = group == RuleGroup ? ExamRuleGroup : RuleGroup;
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine($"Get-NetFirewallRule -Group '{group}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
            if (group == RuleGroup) sb.AppendLine($"Get-NetFirewallRule -DisplayName '{FirewallRules.LegacyIsolationRules}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
            // Önceden kapalı olan profiller yeniden kapatılır (yalnızca bilinen profil adları); öteki grup (karantina /
            // sınav modu) sürüyorsa profillere dokunulmaz
            string[] profiles = profilesToDisable.Where(p => FirewallRules.Profiles.Contains(p)).Distinct().ToArray();
            if (profiles.Length > 0)
            {
                sb.AppendLine($"if (@(Get-NetFirewallRule -Group '{other}' -ErrorAction SilentlyContinue).Count -eq 0) {{");
                foreach (string profile in profiles) sb.AppendLine($"    Set-NetFirewallProfile -Profile {profile} -Enabled False");
                sb.AppendLine("}");
            }
            sb.AppendLine("'OK'");
            return sb.ToString();
        }

        // Enable betiğinin son satırı: karantinadan önceki profil durumları. Zaten karantinadaysa ilk durum korunur.
        private static void SavePreviousProfiles(string statePath, string output)
        {
            if (File.Exists(statePath)) return;
            string json = output?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("[") || l.StartsWith("{"));
            if (json == null) return;
            if (json.StartsWith("{")) json = "[" + json + "]";
            // Alan adı MSI kaldırmasıyla ortak (FirewallRules.IsolationProfilesKey): kaldırma önceden kapalı profilleri buradan okur
            SecureStore.WriteProtected(statePath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [FirewallRules.IsolationProfilesKey] = JsonDocument.Parse(json).RootElement,
                ["since"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }));
        }

        internal static List<string> ReadPreviouslyDisabledProfiles(string statePath)
        {
            var disabled = new List<string>();
            try
            {
                string text = SecureStore.Read(statePath);
                if (text == null) return disabled;
                using JsonDocument doc = JsonDocument.Parse(text);
                foreach (JsonElement p in doc.RootElement.GetProperty(FirewallRules.IsolationProfilesKey).EnumerateArray())
                    if (p.GetProperty("Enabled").GetString() == "False") disabled.Add(p.GetProperty("Name").GetString());
            }
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException) { }
            return disabled;
        }

        // Servisin betik çalıştırıcısı (yalnızca MachineSeams.Production kurar)
        internal static async Task<(int Exit, string Output)> RunPowerShellAsync(string script)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                psi.ArgumentList.Add(arg);

            using Process p = Process.Start(psi);
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (-1, "PowerShell 2 dakikada bitmedi");
            }
            return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
        }
    }
}
