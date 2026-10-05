using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace POpsAgent
{
    // Laboratuvar içi eş önbelleği, ajan tarafı (docs/design/peer-cache.md, seçenek A; X-Agent-Features: peer_cache).
    //  * İmzalı manifest'le doğrulanmış güncelleme paketi C:\POpsData\cache\<sha256> olarak KOPYALANIR (updater'ın
    //    kullandığı paket yerinde kalır). En çok bir paket tutulur; 2 saat sonra ya da başka bir paketle yeni bir
    //    güncelleme başlayınca silinir. Klasör yalnızca SYSTEM/Administrators'a açıktır.
    //  * Paket varken servis TCP 8817'de salt okunur bir HTTP sunucusu açar (bkz. PeerCacheServer): yalnızca
    //    GET /pops-cache/<sha256>, en çok 4 eşzamanlı istemci, yalnızca yerel alt ağdan. Güvenlik duvarına
    //    "POps Peer Cache" grubunda, yalnızca yerel alt ağdan (LocalSubnet) POpsAgent.exe'ye gelen TCP 8817 için bir izin
    //    kuralı eklenir; sunucu durunca (önbellek boşaldı, karantina, servis duruyor) kural kaldırılır.
    //  * Ağ karantinasında sunulmaz (yalıtım kuralları gelen bağlantıyı zaten engeller). peer_cache_enabled kapalıysa
    //    önbellek silinir, sunulmaz ve eşlerden indirilmez (bkz. AgentCapabilities, PeerDownload).
    // Önbellekteki dosyaya kimse güvenmek zorunda değildir: indiren her ajan boyutu ve SHA-256'yı imzalı manifest'le
    // denetler. Bir eşe dosya göndermek yönetici işlemi değildir; yalnızca POps loguna yazılır.
    [SupportedOSPlatform("windows")]
    public static class PeerCache
    {
        public const int Port = 8817;
        public const string RuleGroup = "POps Peer Cache";
        public const string PathPrefix = "/pops-cache/";
        public const int MaxClients = 4;
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
        public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
        // Güvenlik duvarı betiği başarısız olursa yeniden deneme aralığı (her dakika PowerShell çalışmasın)
        private static readonly TimeSpan FirewallRetry = TimeSpan.FromMinutes(15);
        // Saat geriye alındıysa gelecekte görünen dosya da süresi dolmuş sayılır
        private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

        private static readonly Regex Sha256HexRegex = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);

        public static string Dir => Path.Combine(AgentUpdate.DataDir, "cache");

        // Testler: saat, güvenlik duvarı betiği, dinlenen adres, yerel alt ağ ve karantina denetimi
        internal static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
        internal static Func<string, Task<(int Exit, string Output)>> FirewallRunner { get; set; } = RunPowerShellAsync;
        internal static Func<IPEndPoint> ListenEndpoint { get; set; } = () => new IPEndPoint(IPAddress.IPv6Any, Port);
        internal static Func<IPAddress, bool> IsLocalSubnet { get; set; } = InLocalSubnet;
        internal static Func<bool> IsIsolated { get; set; } = () => NetworkIsolation.IsActive;

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static PeerCacheServer? _server;
        // SHA-256'sı bu süreçte denetlenmiş dosya (yol|boyut|zaman); her dakika yeniden hesaplanmaz
        private static string? _verifiedKey;
        private static bool _ruleAdded;
        // Kural var olabilir mi: bilinmiyorsa (açılış) önbellek klasörünün varlığına bakılır (çökme kalıntısı)
        private static bool? _ruleMayExist;
        private static DateTime _firewallRetryAfter = DateTime.MinValue;
        // Servis duruyor: kapanıştan sonra kuyrukta kalan bir denetim sunucuyu yeniden açmasın
        private static volatile bool _shutdown;

        internal sealed record CachedPackage(string Sha256, string Path, long Size, DateTime CachedAtUtc);

        // Sunulan paketin portu (testler); sunulmuyorsa null
        internal static int? ServingPort => _server?.Port;
        internal static string? ServingSha256 => _server?.Sha256;
        internal static int ActiveClients => _server?.ActiveClients ?? 0;

        // Servis açılışında ve her dakika: süre, yetenek, karantina ve dosya bütünlüğüne göre sunucuyu açar/kapatır
        public static async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await SyncAsync();
                try { await Task.Delay(CheckInterval, token); }
                catch (OperationCanceledException) { return; }
            }
        }

        public static async Task SyncAsync()
        {
            await Gate.WaitAsync();
            try { await SyncLockedAsync(); }
            catch (Exception ex) { POpsHelpers.Log("PEERCACHE", $"Eş önbelleği denetlenemedi: {ex.Message}", true); }
            finally { Gate.Release(); }
        }

        // Servis dururken: sunucu kapanır, güvenlik duvarı kuralı kaldırılır; önbellek dosyası kalır (yeni sürüm sürdürür)
        public static async Task ShutdownAsync()
        {
            _shutdown = true;
            await Gate.WaitAsync();
            try { await StopServingAsync("servis duruyor", force: true); }
            catch (Exception ex) { POpsHelpers.Log("PEERCACHE", $"Eş önbelleği kapatılamadı: {ex.Message}", true); }
            finally { Gate.Release(); }
        }

        // Yeni bir güncelleme başlıyor (manifest doğrulandı, sürüm yeni): başka bir paket silinir. Aynı paket yerinde kalır,
        // yerel kaynak olarak kullanılır (bkz. TryCopyToAsync).
        public static async Task OnUpdateStartingAsync(string sha256)
        {
            await Gate.WaitAsync();
            try
            {
                if (Directory.Exists(Dir))
                    foreach (string file in Directory.GetFiles(Dir))
                        if (!string.Equals(Path.GetFileName(file), sha256, StringComparison.Ordinal))
                            Delete(file, "yeni güncelleme başladı");
                await SyncLockedAsync();
            }
            catch (Exception ex) { POpsHelpers.Log("PEERCACHE", $"Eş önbelleği temizlenemedi: {ex.Message}", true); }
            finally { Gate.Release(); }
        }

        // Doğrulanmış paketin önbelleğe kopyası (paket yerinde kalır). Kopya yeniden ölçülür; tutmazsa yazılmaz.
        // Sunum burada başlamaz: servis bu sırada güncelleme için duracaktır; açılışta ya da dakikalık denetimde başlar.
        public static async Task<bool> StoreAsync(string verifiedPackage, string sha256, long size)
        {
            if (!AgentCapabilities.PeerCacheEnabled || !Sha256HexRegex.IsMatch(sha256 ?? "")) return false;
            await Gate.WaitAsync();
            string target = Path.Combine(Dir, sha256!);
            string tmp = target + ".tmp";
            try
            {
                EnsureDirectory();
                foreach (string file in Directory.GetFiles(Dir))
                    if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase)) Delete(file, "en çok bir paket tutulur");
                File.Copy(verifiedPackage, tmp, true);
                (long length, byte[] digest) = HashFile(tmp);
                if (length != size || !CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(sha256!)))
                {
                    TryDelete(tmp);
                    POpsHelpers.Log("PEERCACHE", "Önbellek kopyası paketle uyuşmadı; önbelleğe alınmadı.", true);
                    return false;
                }
                File.Move(tmp, target, true);
                DateTime now = Clock();
                File.SetLastWriteTimeUtc(target, now);
                _verifiedKey = Key(new FileInfo(target));
                POpsHelpers.Log("PEERCACHE", $"Paket eşler için önbelleğe alındı: {target} ({size} bayt); {Lifetime.TotalHours:0} saat ya da sonraki güncellemeye kadar.");
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(tmp);
                POpsHelpers.Log("PEERCACHE", $"Paket önbelleğe alınamadı: {ex.Message}", true);
                return false;
            }
            finally { Gate.Release(); }
        }

        // Aynı paket önbellekteyse hedefe kopyalanır (çağıran imzalı boyut ve SHA-256'yı yine denetler)
        public static async Task<bool> TryCopyToAsync(string sha256, string destination)
        {
            if (!AgentCapabilities.PeerCacheEnabled || !Sha256HexRegex.IsMatch(sha256 ?? "")) return false;
            await Gate.WaitAsync();
            try
            {
                string source = Path.Combine(Dir, sha256!);
                if (!File.Exists(source)) return false;
                File.Copy(source, destination, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                POpsHelpers.Log("PEERCACHE", $"Önbellekteki paket kopyalanamadı: {ex.Message}", true);
                return false;
            }
            finally { Gate.Release(); }
        }

        // ------------------------------------------------------------------------------------------
        private static async Task SyncLockedAsync()
        {
            CachedPackage? package = Scan();
            if (package != null && !AgentCapabilities.PeerCacheEnabled)
            {
                Delete(package.Path, "peer_cache_enabled kapalı");
                package = null;
            }
            else if (package != null && IsExpired(package, Clock()))
            {
                Delete(package.Path, $"{Lifetime.TotalHours:0} saat doldu");
                package = null;
            }
            if (package != null && !IsIntact(package))
            {
                Delete(package.Path, "SHA-256 dosya adıyla uyuşmuyor");
                package = null;
            }

            if (package == null) await StopServingAsync("önbellek boş");
            else if (_shutdown) await StopServingAsync("servis duruyor", force: true);
            else if (IsIsolated()) await StopServingAsync("ağ karantinası sürüyor");
            else await StartServingAsync(package);

            if (package == null && _ruleMayExist == false) TryDeleteEmptyDirectory();
        }

        internal static bool IsExpired(CachedPackage package, DateTime nowUtc)
        {
            TimeSpan age = nowUtc - package.CachedAtUtc;
            return age >= Lifetime || age < -ClockSkew;
        }

        // Geçerli adlı en yeni paket; diğer dosyalar (eski paket, yarım .tmp) silinir
        private static CachedPackage? Scan()
        {
            if (!Directory.Exists(Dir)) return null;
            CachedPackage? newest = null;
            foreach (string file in Directory.GetFiles(Dir))
            {
                string name = Path.GetFileName(file);
                if (!Sha256HexRegex.IsMatch(name))
                {
                    Delete(file, "önbellek dosyası değil");
                    continue;
                }
                var info = new FileInfo(file);
                var package = new CachedPackage(name, file, info.Length, info.LastWriteTimeUtc);
                if (newest == null || package.CachedAtUtc > newest.CachedAtUtc)
                {
                    if (newest != null) Delete(newest.Path, "en çok bir paket tutulur");
                    newest = package;
                }
                else Delete(file, "en çok bir paket tutulur");
            }
            return newest;
        }

        // Dosyanın içeriği adındaki SHA-256'yla aynı mı (bu süreçte bir kez; ör. yeni sürüm açılışta)
        private static bool IsIntact(CachedPackage package)
        {
            string key = Key(new FileInfo(package.Path));
            if (key == _verifiedKey) return true;
            (_, byte[] digest) = HashFile(package.Path);
            if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(package.Sha256))) return false;
            _verifiedKey = key;
            return true;
        }

        private static string Key(FileInfo info) => $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

        private static async Task StartServingAsync(CachedPackage package)
        {
            if (_server != null && _server.Sha256 != package.Sha256)
            {
                await _server.StopAsync();
                _server.Dispose();
                _server = null;
            }
            if (_server == null)
            {
                var server = new PeerCacheServer(package.Sha256, package.Path, ListenEndpoint(), address => IsLocalSubnet(address), MaxClients);
                try { server.Start(); }
                catch (SocketException ex)
                {
                    server.Dispose();
                    POpsHelpers.Log("PEERCACHE", $"Eş önbelleği dinlenemedi (port {Port}): {ex.Message}", true);
                    return;
                }
                _server = server;
                DateTime until = (package.CachedAtUtc + Lifetime).ToLocalTime();
                POpsHelpers.Log("PEERCACHE", $"Eş önbelleği açık: TCP {server.Port}, {PathPrefix}{package.Sha256}, en geç {until:HH:mm}'e kadar (yalnızca yerel alt ağ).");
            }
            await EnsureRuleAsync();
        }

        // force: servis duruyor; başarısız betik sonrası bekleme süresi beklenmez (kural kalmasın)
        private static async Task StopServingAsync(string reason, bool force = false)
        {
            if (_server != null)
            {
                await _server.StopAsync();
                _server.Dispose();
                _server = null;
                POpsHelpers.Log("PEERCACHE", $"Eş önbelleği kapandı ({reason}).");
            }
            _ruleMayExist ??= Directory.Exists(Dir);
            if (_ruleMayExist == true && (force || Clock() >= _firewallRetryAfter))
            {
                (int exit, string output) = await RunFirewallAsync(BuildRemoveRuleScript());
                if (exit == 0)
                {
                    _ruleMayExist = false;
                    _ruleAdded = false;
                }
                else FirewallFailed("kaldırılamadı", exit, output);
            }
        }

        private static async Task EnsureRuleAsync()
        {
            if (_ruleAdded || Clock() < _firewallRetryAfter) return;
            _ruleMayExist = true;
            (int exit, string output) = await RunFirewallAsync(BuildAddRuleScript(ProgramPath()));
            if (exit == 0)
            {
                _ruleAdded = true;
                POpsHelpers.Log("PEERCACHE", $"Güvenlik duvarı: \"{RuleGroup}\" kuralı eklendi (TCP {Port}, yalnızca yerel alt ağ, POpsAgent.exe).");
            }
            else FirewallFailed("eklenemedi", exit, output);
        }

        private static async Task<(int Exit, string Output)> RunFirewallAsync(string script)
        {
            try { return await FirewallRunner(script); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is IOException)
            {
                return (-1, ex.Message);
            }
        }

        private static void FirewallFailed(string what, int exit, string output)
        {
            _firewallRetryAfter = Clock() + FirewallRetry;
            POpsHelpers.Log("PEERCACHE", $"Güvenlik duvarı kuralı {what} (çıkış {exit}: {LogText.Safe(output, 200)}); {FirewallRetry.TotalMinutes:0} dk sonra yeniden denenecek.", true);
        }

        private static string ProgramPath() => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "POpsAgent.exe");

        // Kural yalnızca bu programa, yalnızca yerel alt ağdan, yalnızca TCP 8817 gelen bağlantıya izin verir. Karantina
        // grubundan ("POps Isolation") ayrı bir gruptur; yalıtımın engelleme kuralları izin kurallarının hepsini geçer.
        internal static string BuildAddRuleScript(string program) => $@"$ErrorActionPreference = 'Stop'
$group = {BitsDownload.Quote(RuleGroup)}
Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Group $group -DisplayName {BitsDownload.Quote($"POps Peer Cache (TCP {Port})")} -Direction Inbound -Action Allow -Protocol TCP -LocalPort {Port} -RemoteAddress LocalSubnet -Program {BitsDownload.Quote(program)} -Profile Any | Out-Null
'OK'
";

        internal static string BuildRemoveRuleScript() => $@"$ErrorActionPreference = 'Stop'
Get-NetFirewallRule -Group {BitsDownload.Quote(RuleGroup)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule
'OK'
";

        // ------------------------------------------------------------------------------------------
        // Yerel alt ağ: bu bilgisayarın çalışan arabirimlerinden birinin ağında mı (güvenlik duvarının LocalSubnet'i gibi)
        // ------------------------------------------------------------------------------------------
        internal static bool InLocalSubnet(IPAddress address)
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }
                foreach (UnicastIPAddressInformation unicast in props.UnicastAddresses)
                    if (SameSubnet(unicast.Address, unicast.PrefixLength, address)) return true;
            }
            return false;
        }

        // Çok geniş önekler (IPv4 /8'den, IPv6 /64'ten geniş; ör. tünel arabirimleri) yerel alt ağ sayılmaz
        internal static bool SameSubnet(IPAddress network, int prefixLength, IPAddress address)
        {
            IPAddress n = Normalize(network), a = Normalize(address);
            if (n.AddressFamily != a.AddressFamily) return false;
            byte[] nb = n.GetAddressBytes(), ab = a.GetAddressBytes();
            int min = n.AddressFamily == AddressFamily.InterNetwork ? 8 : 64;
            if (prefixLength < min || prefixLength > nb.Length * 8) return false;
            int full = prefixLength / 8, rest = prefixLength % 8;
            for (int i = 0; i < full; i++)
                if (nb[i] != ab[i]) return false;
            if (rest == 0) return true;
            int mask = (0xFF << (8 - rest)) & 0xFF;
            return (nb[full] & mask) == (ab[full] & mask);
        }

        internal static IPAddress Normalize(IPAddress address)
        {
            IPAddress a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0 ? new IPAddress(a.GetAddressBytes()) : a;
        }

        // ------------------------------------------------------------------------------------------
        private static void EnsureDirectory()
        {
            var dir = new DirectoryInfo(Dir);
            if (dir.Exists) dir.SetAccessControl(SecureStore.ProtectedDirectorySecurity());
            else dir.Create(SecureStore.ProtectedDirectorySecurity());
        }

        private static void TryDeleteEmptyDirectory()
        {
            try
            {
                if (Directory.Exists(Dir) && Directory.GetFileSystemEntries(Dir).Length == 0) Directory.Delete(Dir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static void Delete(string path, string reason)
        {
            try
            {
                File.Delete(path);
                if (Sha256HexRegex.IsMatch(Path.GetFileName(path))) POpsHelpers.Log("PEERCACHE", $"Önbellekteki paket silindi ({reason}): {path}");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                POpsHelpers.Log("PEERCACHE", $"{path} silinemedi: {ex.Message}", true);
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static (long Size, byte[] Sha256) HashFile(string path)
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return (stream.Length, SHA256.HashData(stream));
        }

        // Testler: her test temiz başlar (önceki testin sunucusu kapanır, bellekteki durum unutulur)
        internal static void ResetState()
        {
            PeerCacheServer? server = _server;
            _server = null;
            if (server != null)
            {
                server.StopAsync().GetAwaiter().GetResult();
                server.Dispose();
            }
            _verifiedKey = null;
            _ruleAdded = false;
            _ruleMayExist = null;
            _firewallRetryAfter = DateTime.MinValue;
            _shutdown = false;
        }

        private static async Task<(int Exit, string Output)> RunPowerShellAsync(string script)
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
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell başlatılamadı");
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return (-1, "PowerShell 2 dakikada bitmedi");
            }
            return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
        }
    }
}
