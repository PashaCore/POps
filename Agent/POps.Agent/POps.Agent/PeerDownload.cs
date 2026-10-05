using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace POpsAgent
{
    // update_agent "peers": [{"hw_id","url"}] (bkz. PeerCache, docs/design/peer-cache.md). Paket önce sıradaki eşlerden,
    // sonra sunucudan indirilir; her kaynak imzalı manifest'teki boyut ve SHA-256'yla denetlenir. Kötü bir eş en çok bir
    // deneme kaybettirir, asla kötü bir kurulum değil.
    //  * Adres yalnızca http://<özel ya da bağlantı-yerel IPv4/IPv6>:<port>/pops-cache/<manifest'teki sha256> olabilir ve
    //    bu bilgisayarın yerel alt ağında olmalıdır (eşin güvenlik duvarı kuralı da yalnızca yerel alt ağa açıktır).
    //    Ad, https, başka yol, sorgu, kullanıcı bilgisi, genel ya da geri döngü adresi reddedilir; en çok 5 eş.
    //  * Eşe hiçbir başlık (cihaz secret'ı, sürüm, çerez) gitmez; vekil sunucu kullanılmaz, yönlendirme izlenmez.
    //  * Bağlantı için 3 sn. Yanıt başlığı için 75 sn: meşgul eş isteği boş aktarım yeri için 60 sn sıraya alır (bkz.
    //    PeerCacheServer). Yanıt 200 ve Content-Length imzalı boyut olmalı; aktarımda 15 sn veri gelmezse ya da eş
    //    başına toplam 5 dk dolarsa sıradaki kaynağa geçilir.
    [SupportedOSPlatform("windows")]
    public static class PeerDownload
    {
        public const int MaxPeers = 5;
        public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan PeerTimeout = TimeSpan.FromMinutes(5);
        // Testler: yanıt başlığı ve veri bekleme süreleri kısaltılır
        internal static TimeSpan HeaderTimeout { get; set; } = DefaultHeaderTimeout;
        internal static TimeSpan StallTimeout { get; set; } = DefaultStallTimeout;
        internal static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(75);
        internal static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(15);
        private const int MaxUrlLength = 256;

        private static readonly Regex HwIdRegex = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        // Testler: sahte eş 127.0.0.1'de çalışır
        internal static bool AllowLoopbackPeers { get; set; }

        public sealed record Peer(string HwId, Uri Url);

        // Emirdeki eşler, sırasıyla; geçersiz olanlar loglanıp atlanır. Alan yoksa ya da dizi değilse boş liste.
        public static List<Peer> Parse(JsonElement command, string sha256)
        {
            var peers = new List<Peer>();
            if (command.ValueKind != JsonValueKind.Object || !command.TryGetProperty("peers", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return peers;
            int seen = 0;
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (++seen > MaxPeers)
                {
                    POpsHelpers.Log("UPDATE", $"Emirde {MaxPeers}'ten fazla eş var; fazlası denenmeyecek.", true);
                    break;
                }
                string? hwId = Str(item, "hw_id");
                string? url = Str(item, "url");
                string label = hwId != null && HwIdRegex.IsMatch(hwId) ? hwId : "?";
                if (TryValidate(url, sha256, out Uri? uri, out string reason)) peers.Add(new Peer(label, uri!));
                else POpsHelpers.Log("UPDATE", $"[GÜVENLİK] Eş adresi reddedildi ({label}, {LogText.Safe(url, 120)}): {reason}.", true);
            }
            return peers;
        }

        public static bool TryValidate(string? url, string sha256, out Uri? uri, out string reason)
        {
            uri = null;
            if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength) { reason = "adres yok ya da çok uzun"; return false; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)) { reason = "geçerli bir adres değil"; return false; }
            if (parsed.Scheme != Uri.UriSchemeHttp) { reason = "yalnızca http"; return false; }
            if (parsed.HostNameType != UriHostNameType.IPv4 && parsed.HostNameType != UriHostNameType.IPv6) { reason = "ana bilgisayar IP adresi değil"; return false; }
            if (parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0) { reason = "kullanıcı bilgisi, sorgu ya da parça var"; return false; }
            if (!string.Equals(parsed.AbsolutePath, PeerCache.PathPrefix + sha256, StringComparison.Ordinal)) { reason = "yol /pops-cache/<paketin sha256'sı> değil"; return false; }
            if (!IPAddress.TryParse(parsed.DnsSafeHost, out IPAddress? address)) { reason = "IP adresi okunamadı"; return false; }

            IPAddress a = PeerCache.Normalize(address);
            bool loopback = IPAddress.IsLoopback(a);
            if (loopback && !AllowLoopbackPeers) { reason = "geri döngü adresi"; return false; }
            if (!loopback && !IsPrivateOrLinkLocal(a)) { reason = "özel ya da bağlantı-yerel bir adres değil"; return false; }
            if (!PeerCache.IsLocalSubnet(a)) { reason = "bu bilgisayarın yerel alt ağında değil"; return false; }
            uri = parsed;
            reason = "";
            return true;
        }

        // 10/8, 172.16/12, 192.168/16, 169.254/16; IPv6 fc00::/7 (ULA) ve fe80::/10
        internal static bool IsPrivateOrLinkLocal(IPAddress address)
        {
            IPAddress a = PeerCache.Normalize(address);
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return a.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
            return false;
        }

        // Eşler sırayla denenir; doğrulanmış paket hedefe yazılır. Dönen: paketi veren eş, olmadıysa null (hedef silinir).
        public static async Task<Peer?> DownloadAsync(IReadOnlyList<Peer>? peers, ReleaseVerifier.Artifact expected, string target)
        {
            if (peers == null || peers.Count == 0) return null;
            using var handler = new SocketsHttpHandler
            {
                ConnectTimeout = ConnectTimeout,
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                PreAuthenticate = false,
                AutomaticDecompression = DecompressionMethods.None,
                MaxConnectionsPerServer = 1,
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            foreach (Peer peer in peers)
            {
                POpsHelpers.Log("UPDATE", $"Eşten indiriliyor: {peer.HwId} ({peer.Url})");
                string? failure = await TryPeerAsync(client, peer, expected, target);
                if (failure == null) return peer;
                TryDelete(target);
                POpsHelpers.Log("UPDATE", $"Eşten alınamadı ({peer.HwId}): {failure}; sıradaki kaynak deneniyor.", true);
            }
            return null;
        }

        // null: paket indi ve imzalı boyut + SHA-256 tuttu; aksi halde neden
        private static async Task<string?> TryPeerAsync(HttpClient client, Peer peer, ReleaseVerifier.Artifact expected, string target)
        {
            using var cts = new CancellationTokenSource(HeaderTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, peer.Url)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.StatusCode != HttpStatusCode.OK) return $"HTTP {(int)response.StatusCode}";
                long? announced = response.Content.Headers.ContentLength;
                if (announced != expected.Size) return $"Content-Length {announced?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "yok"}, imzalı boyut {expected.Size}";

                cts.CancelAfter(PeerTimeout);
                // Veri gelmeden geçen süre: her okumada yeniden başlar
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                await using (Stream input = await response.Content.ReadAsStreamAsync(cts.Token))
                await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        int read = await input.ReadAsync(buffer, stall.Token);
                        if (read == 0) break;
                        total += read;
                        if (total > expected.Size) return "imzalı boyuttan büyük";
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    }
                }
                if (total != expected.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expected.Sha256)))
                    return $"imzalı manifest'le uyuşmuyor (boyut {total}/{expected.Size} ya da SHA-256)";
                return null;
            }
            catch (OperationCanceledException) { return "süre doldu (yanıt ya da veri gelmedi)"; }
            catch (HttpRequestException ex) { return ex.Message; }
            catch (IOException ex) { return ex.Message; }
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
