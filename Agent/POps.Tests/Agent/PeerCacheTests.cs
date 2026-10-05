using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Laboratuvar eş önbelleği (docs/agent.md "Peer cache contract", docs/design/peer-cache.md seçenek A): önbellek,
    // sunucunun peer_cache bayrağı, salt okunur sunucu, güvenlik duvarı kuralı (sahte çalıştırıcı), eşlerden indirme,
    // karantina / sınav modu ve yetenek anahtarı. Sunucu yalnızca 127.0.0.1'de rastgele portta açılır.
    [Collection(SharedStateCollection.Name)]
    public class PeerCacheTests : SharedStateTestBase, IDisposable
    {
        private const string MsiName = "POps-Agent-9.9.9-win-x64.msi";
        private readonly byte[] _package = RandomNumberGenerator.GetBytes(200_000);
        private readonly List<string> _firewall = new List<string>();
        private readonly List<Dictionary<string, object>> _stages = new List<Dictionary<string, object>>();
        private readonly List<PeerCacheServer> _peers = new List<PeerCacheServer>();
        private readonly Ed25519PrivateKeyParameters _key;
        private readonly List<TcpListener> _rawPeers = new List<TcpListener>();
        private DateTime _now = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
        private bool _isolated;
        // Eş önbelleği sunucusu kaç kez dinlemeye başladı (ListenEndpoint sahtesi)
        private int _listens;

        public PeerCacheTests()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("peer-data");
            SecureStore.Dir = TestEnvironment.NewDir("peer-secure");
            AgentCapabilities.Load(); // dosya yok: hepsi açık
            PeerCache.FirewallRunner = script =>
            {
                lock (_firewall) _firewall.Add(script);
                return Task.FromResult((0, "OK"));
            };
            PeerCache.Clock = () => _now;
            PeerCache.IsIsolated = () => _isolated;
            PeerCache.IsLocalSubnet = _ => true;
            PeerCache.ListenEndpoint = () =>
            {
                Interlocked.Increment(ref _listens);
                return new IPEndPoint(IPAddress.Loopback, 0);
            };

            var generator = new Ed25519KeyPairGenerator();
            generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = generator.GenerateKeyPair();
            _key = (Ed25519PrivateKeyParameters)pair.Private;
            AgentUpdate.TrustedKeyOverride = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
            AgentUpdate.InstalledVersionOverride = "0.1.22-alpha";
            AgentUpdate.LaunchOverride = (_, _, _) => true;
        }

        public void Dispose()
        {
            foreach (PeerCacheServer peer in _peers)
            {
                peer.StopAsync().GetAwaiter().GetResult();
                peer.Dispose();
            }
            foreach (TcpListener raw in _rawPeers) raw.Stop();
            PeerCache.ResetState();
            AgentUpdate.TrustedKeyOverride = null;
            AgentUpdate.InstalledVersionOverride = null;
            AgentUpdate.LaunchOverride = null;
            // Sonraki testler yetenekleri açık bulsun
            SecureStore.Dir = TestEnvironment.NewDir("peer-secure-reset");
            AgentCapabilities.Load();
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
        }

        // ---------------------------------------------------------------------------------------- yardımcılar
        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private ReleaseVerifier.Artifact Artifact(byte[] bytes = null) =>
            new ReleaseVerifier.Artifact { Name = MsiName, Size = (bytes ?? _package).Length, Sha256 = Sha(bytes ?? _package) };

        private string CachePath(byte[] bytes = null) => Path.Combine(PeerCache.Dir, Sha(bytes ?? _package));

        // peerCache: sunucunun "peer_cache": true bayrağı (paketi tut ve sun); false ise alan hiç yok
        private JsonElement Command(byte[] bytes = null, object peers = null, bool peerCache = true)
        {
            bytes ??= _package;
            byte[] manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schema = "pops-manifest/1", version = "9.9.9", tag = "v9.9.9", released_at = 1791200000,
                artifacts = new object[] { new { name = MsiName, sha256 = Sha(bytes), size = bytes.Length } },
            }));
            var signer = new Ed25519Signer();
            signer.Init(true, _key);
            signer.BlockUpdate(manifest, 0, manifest.Length);
            var message = new Dictionary<string, object>
            {
                ["action"] = "update_agent",
                ["manifest"] = Convert.ToBase64String(manifest),
                ["manifest_sig"] = Convert.ToBase64String(signer.GenerateSignature()),
            };
            if (peers != null) message["peers"] = peers;
            if (peerCache) message["peer_cache"] = true;
            return JsonDocument.Parse(JsonSerializer.Serialize(message)).RootElement.Clone();
        }

        private sealed class Server : HttpMessageHandler
        {
            public int Requests;
            public Func<HttpResponseMessage> Respond { get; set; }
            public Action OnRequest { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Requests);
                OnRequest?.Invoke();
                return Task.FromResult(Respond());
            }
        }

        private static Server Serving(byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
            new Server { Respond = () => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) } };

        private Task<bool> Collect(Dictionary<string, object> message)
        {
            lock (_stages) _stages.Add(message);
            return Task.FromResult(true);
        }

        private string[] Stages() { lock (_stages) return _stages.Select(m => (string)m["stage"]).ToArray(); }

        private string Detail(string stage)
        {
            lock (_stages) return _stages.Single(m => (string)m["stage"] == stage).TryGetValue("detail", out object d) ? (string)d : null;
        }

        private AgentUpdate.UpdateCommand Update() => new AgentUpdate.UpdateCommand { Report = Collect };

        private Task Run(JsonElement command, Server server) =>
            AgentUpdate.HandleUpdateCommandAsync(command, new HttpClient(server), "https://pops.example", Collect);

        private async Task<string> StoreAsync(byte[] bytes = null)
        {
            bytes ??= _package;
            string file = Path.Combine(TestEnvironment.NewDir("peer-src"), MsiName);
            File.WriteAllBytes(file, bytes);
            Assert.True(await PeerCache.StoreAsync(file, Sha(bytes), bytes.Length));
            return file;
        }

        // Testteki "başka bir PC": verilen dosyayı 127.0.0.1'de sunan ayrı bir sunucu
        private PeerCacheServer StartPeer(byte[] content, string sha256, Func<IPAddress, bool> isLocal = null, int maxClients = 4, TimeSpan? slotWait = null)
        {
            string file = Path.Combine(TestEnvironment.NewDir("peer-remote"), sha256);
            File.WriteAllBytes(file, content);
            var peer = new PeerCacheServer(sha256, file, new IPEndPoint(IPAddress.Loopback, 0), isLocal ?? (_ => true), maxClients, slotWait: slotWait);
            peer.Start();
            _peers.Add(peer);
            return peer;
        }

        private static string PeerUrl(int port, string sha256) => $"http://127.0.0.1:{port}{PeerCache.PathPrefix}{sha256}";

        private static PeerDownload.Peer Peer(string hwId, int port, string sha256) => new PeerDownload.Peer(hwId, new Uri(PeerUrl(port, sha256)));

        private sealed class RawResponse
        {
            public int Status;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] Body = Array.Empty<byte>();
        }

        // Ham HTTP isteği (sunucunun ayrıştırmasını HttpClient'ın düzeltmesi olmadan denemek için)
        private static async Task<RawResponse> RawAsync(int port, string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var all = new MemoryStream();
            await stream.CopyToAsync(all, cts.Token);
            byte[] bytes = all.ToArray();
            int split = IndexOf(bytes, Encoding.ASCII.GetBytes("\r\n\r\n"));
            Assert.True(split > 0, "yanıt başlığı yok");
            string[] lines = Encoding.ASCII.GetString(bytes, 0, split).Split("\r\n");
            var response = new RawResponse { Status = int.Parse(lines[0].Split(' ')[1]), Body = bytes.Skip(split + 4).ToArray() };
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                response.Headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
            }
            return response;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
            return -1;
        }

        private static string Get(string path) => $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";

        // Kurallara uymayan bir eş: tek bağlantı kabul eder, isteği okur, respond'u çalıştırır, sonra kapatır
        private int RawPeer(Func<NetworkStream, Task> respond)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _rawPeers.Add(listener);
            _ = Task.Run(async () =>
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync();
                    using NetworkStream stream = client.GetStream();
                    var head = new MemoryStream();
                    byte[] one = new byte[1];
                    while (!head.ToArray().AsSpan().EndsWith("\r\n\r\n"u8) && await stream.ReadAsync(one) == 1) head.WriteByte(one[0]);
                    await respond(stream);
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static async Task<bool> PortClosedAsync(int port)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return false;
            }
            catch (SocketException) { return true; }
        }

        private string[] Firewall() { lock (_firewall) return _firewall.ToArray(); }

        // ---------------------------------------------------------------------------------------- önbellek
        [Fact]
        public async Task VerifiedUpdate_IsCopiedToTheCache_AndThePackageStaysForTheUpdater()
        {
            string launchedPackage = null;
            bool packageThereAtLaunch = false;
            byte[] cachedAtLaunch = null;
            AgentUpdate.LaunchOverride = (msi, _, _) =>
            {
                launchedPackage = msi;
                packageThereAtLaunch = File.Exists(msi);
                cachedAtLaunch = File.ReadAllBytes(CachePath());
                return true;
            };
            await Run(Command(), Serving(_package));

            Assert.Equal(new[] { "received", "downloaded", "verified", "updater_started" }, Stages());
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));
            Assert.True(packageThereAtLaunch);
            Assert.Equal(Path.Combine(AgentUpdate.UpdatesDir, MsiName), launchedPackage);
            Assert.Equal(_package, cachedAtLaunch);
            Assert.Equal(new[] { Sha(_package) }, Directory.GetFiles(PeerCache.Dir).Select(Path.GetFileName));
            // Klasör yalnızca SYSTEM/Administrators'a açık (testte "SYSTEM" testi çalıştıran kullanıcıdır)
            Assert.True(SecureStore.IsLockedDown(new DirectoryInfo(PeerCache.Dir).GetAccessControl()));
            // Sunum servis açılışında / dakikalık denetimde başlar (servis bu sırada güncelleme için duracaktır)
            Assert.Null(PeerCache.ServingPort);
        }

        [Fact]
        public async Task FailedVerification_WritesNothingToTheCache()
        {
            byte[] evil = (byte[])_package.Clone();
            evil[100] ^= 0xFF;
            await Run(Command(), Serving(evil));

            Assert.Equal(new[] { "received", "downloaded", "rejected" }, Stages());
            Assert.False(File.Exists(CachePath()));
            Assert.False(File.Exists(CachePath(evil)));
        }

        [Fact]
        public async Task Store_RefusesACopyThatDoesNotMatch()
        {
            string file = Path.Combine(TestEnvironment.NewDir("peer-src"), MsiName);
            File.WriteAllBytes(file, _package.Take(1000).ToArray());
            Assert.False(await PeerCache.StoreAsync(file, Sha(_package), _package.Length));
            Assert.False(File.Exists(CachePath()));
            Assert.Empty(Directory.GetFiles(PeerCache.Dir));
        }

        [Fact]
        public async Task CachedPackage_ExpiresAfterTwoHours_AndServingStops()
        {
            await StoreAsync();
            await PeerCache.SyncAsync();
            int port = Assert.IsType<int>(PeerCache.ServingPort);

            _now = _now.AddHours(2).AddMinutes(-1);
            await PeerCache.SyncAsync();
            Assert.True(File.Exists(CachePath()));
            Assert.Equal(port, PeerCache.ServingPort);

            _now = _now.AddMinutes(1);
            await PeerCache.SyncAsync();
            Assert.False(File.Exists(CachePath()));
            Assert.Null(PeerCache.ServingPort);
            Assert.True(await PortClosedAsync(port));
            Assert.Contains("Remove-NetFirewallRule", Firewall().Last());
            Assert.DoesNotContain("New-NetFirewallRule", Firewall().Last());
            // Kural kalkınca boş klasör de kalkar: sonraki açılış güvenlik duvarına hiç dokunmaz
            Assert.False(Directory.Exists(PeerCache.Dir));
        }

        [Fact]
        public async Task ClockTurnedBack_CountsAsExpired()
        {
            await StoreAsync();
            _now = _now.AddHours(-1);
            await PeerCache.SyncAsync();
            Assert.False(File.Exists(CachePath()));
        }

        [Fact]
        public async Task NextUpdate_DeletesTheOtherPackage_BeforeDownloading()
        {
            await StoreAsync();
            await PeerCache.SyncAsync();
            Assert.NotNull(PeerCache.ServingPort);

            byte[] next = RandomNumberGenerator.GetBytes(150_000);
            bool oldGoneAtDownload = false;
            Server server = Serving(next);
            server.OnRequest = () => oldGoneAtDownload = !File.Exists(CachePath()) && PeerCache.ServingPort == null;
            await Run(Command(next), server);

            Assert.True(oldGoneAtDownload);
            Assert.Equal(new[] { Sha(next) }, Directory.GetFiles(PeerCache.Dir).Select(Path.GetFileName));
            Assert.Equal(next, File.ReadAllBytes(CachePath(next)));
        }

        [Fact]
        public async Task SameUpdateAgain_UsesTheCachedCopy_WithoutDownloading()
        {
            await StoreAsync();
            Server server = Serving(Array.Empty<byte>(), HttpStatusCode.NotFound);
            await Run(Command(), server);

            Assert.Equal(0, server.Requests);
            Assert.Equal(new[] { "received", "downloaded", "verified", "updater_started" }, Stages());
            Assert.Equal(AgentUpdate.SourceCache, Detail("downloaded"));
            Assert.True(File.Exists(CachePath()));
        }

        [Fact]
        public async Task TamperedCacheFile_IsDeleted_NotServed()
        {
            await StoreAsync();
            PeerCache.ResetState(); // servis yeniden başladı: dosya bu süreçte denetlenmedi
            byte[] tampered = (byte[])_package.Clone();
            tampered[5] ^= 0x01;
            File.WriteAllBytes(CachePath(), tampered);
            File.SetLastWriteTimeUtc(CachePath(), _now);

            await PeerCache.SyncAsync();
            Assert.False(File.Exists(CachePath()));
            Assert.Null(PeerCache.ServingPort);
        }

        [Fact]
        public async Task AfterRestart_TheIntactPackageIsServedAgain()
        {
            await StoreAsync();
            PeerCache.ResetState();
            await PeerCache.SyncAsync();
            Assert.Equal(Sha(_package), PeerCache.ServingSha256);
        }

        [Fact]
        public async Task JunkAndExtraPackages_AreRemoved_OnlyTheNewestStays()
        {
            await StoreAsync();
            byte[] older = RandomNumberGenerator.GetBytes(1000);
            File.WriteAllBytes(CachePath(older), older);
            File.SetLastWriteTimeUtc(CachePath(older), _now.AddMinutes(-30));
            File.WriteAllText(Path.Combine(PeerCache.Dir, "notes.txt"), "x");

            await PeerCache.SyncAsync();
            Assert.Equal(new[] { Sha(_package) }, Directory.GetFiles(PeerCache.Dir).Select(Path.GetFileName));
        }

        // ---------------------------------------------------------------------------------------- sunucu
        [Fact]
        public async Task Server_ServesExactlyThePackage_WithGet()
        {
            await StoreAsync();
            await PeerCache.SyncAsync();
            int port = PeerCache.ServingPort.Value;

            RawResponse ok = await RawAsync(port, Get(PeerCache.PathPrefix + Sha(_package)));
            Assert.Equal(200, ok.Status);
            Assert.Equal("application/octet-stream", ok.Headers["Content-Type"]);
            Assert.Equal(_package.Length.ToString(), ok.Headers["Content-Length"]);
            Assert.Equal(_package, ok.Body);

            // HttpClient ile de (indiren ajanın yaptığı gibi)
            using var http = new HttpClient();
            Assert.Equal(_package, await http.GetByteArrayAsync(PeerUrl(port, Sha(_package))));
        }

        [Theory]
        [InlineData("GET /pops-cache/{other} HTTP/1.1", 404)]
        [InlineData("GET /pops-cache/ HTTP/1.1", 404)]
        [InlineData("GET / HTTP/1.1", 404)]
        [InlineData("GET /pops-cache HTTP/1.1", 404)]
        [InlineData("GET /pops-cache/{SHA} HTTP/1.1", 404)]
        [InlineData("GET /pops-cache/{sha}?x=1 HTTP/1.1", 404)]
        [InlineData("GET /pops-cache/{sha}/ HTTP/1.1", 404)]
        [InlineData("GET /pops-cache/../pops-cache/{sha} HTTP/1.1", 404)]
        [InlineData("GET /pops-cache/%2e%2e/{sha} HTTP/1.1", 404)]
        [InlineData("GET http://127.0.0.1/pops-cache/{sha} HTTP/1.1", 404)]
        [InlineData("GET /updates/POps-Agent-9.9.9-win-x64.msi HTTP/1.1", 404)]
        [InlineData("POST /pops-cache/{sha} HTTP/1.1", 405)]
        [InlineData("PUT /pops-cache/{sha} HTTP/1.1", 405)]
        [InlineData("DELETE /pops-cache/{sha} HTTP/1.1", 405)]
        [InlineData("GET /pops-cache/{sha} HTTP/2.0", 400)]
        [InlineData("GET /pops-cache/{sha}", 400)]
        [InlineData("hello", 400)]
        public async Task Server_RefusesEverythingElse(string requestLine, int expected)
        {
            await StoreAsync();
            await PeerCache.SyncAsync();
            string sha = Sha(_package);
            string line = requestLine.Replace("{sha}", sha).Replace("{SHA}", sha.ToUpperInvariant()).Replace("{other}", new string('a', 64));
            RawResponse response = await RawAsync(PeerCache.ServingPort.Value, line + "\r\nHost: 127.0.0.1\r\n\r\n");
            Assert.Equal(expected, response.Status);
            Assert.Empty(response.Body);
            if (expected == 405) Assert.Equal("GET, HEAD", response.Headers["Allow"]);
        }

        [Fact]
        public async Task Server_AnswersHead_WithoutABody()
        {
            await StoreAsync();
            await PeerCache.SyncAsync();
            RawResponse head = await RawAsync(PeerCache.ServingPort.Value, $"HEAD {PeerCache.PathPrefix}{Sha(_package)} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            Assert.Equal(200, head.Status);
            Assert.Equal("application/octet-stream", head.Headers["Content-Type"]);
            Assert.Equal(_package.Length.ToString(), head.Headers["Content-Length"]);
            Assert.Empty(head.Body);
        }

        [Fact]
        public async Task Server_RefusesAddressesOutsideTheLocalSubnet()
        {
            await StoreAsync();
            var asked = new List<IPAddress>();
            PeerCache.IsLocalSubnet = address => { lock (asked) asked.Add(address); return false; };
            await PeerCache.SyncAsync();

            RawResponse response = await RawAsync(PeerCache.ServingPort.Value, Get(PeerCache.PathPrefix + Sha(_package)));
            Assert.Equal(403, response.Status);
            Assert.Empty(response.Body);
            Assert.Contains(IPAddress.Loopback, asked);
        }

        [Fact]
        public async Task Server_BusyRequestWaitsForASlot_ThenGetsThePackage()
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PeerCacheServer peer = StartPeer(_package, Sha(_package), maxClients: 1);
            int transfers = 0;
            peer.TransferStarting = () => Interlocked.Increment(ref transfers) == 1 ? hold.Task : Task.CompletedTask;

            Task<RawResponse> first = RawAsync(peer.Port, Get(PeerCache.PathPrefix + Sha(_package)));
            for (int i = 0; i < 500 && peer.ActiveClients < 1; i++) await Task.Delay(10);
            Assert.Equal(1, peer.ActiveClients);

            // İkinci istek hemen 503 almaz: boş yer için bekler
            Task<RawResponse> second = RawAsync(peer.Port, Get(PeerCache.PathPrefix + Sha(_package)));
            for (int i = 0; i < 500 && peer.Connections < 2; i++) await Task.Delay(10);
            await Task.Delay(300);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, peer.ActiveClients);

            hold.SetResult();
            Assert.Equal(_package, (await first).Body);
            RawResponse later = await second;
            Assert.Equal(200, later.Status);
            Assert.Equal(_package, later.Body);
            Assert.Equal(2, transfers);
        }

        [Fact]
        public async Task Server_AnswersBusy_WhenNoSlotFreesInTime()
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PeerCacheServer peer = StartPeer(_package, Sha(_package), maxClients: 1, slotWait: TimeSpan.FromMilliseconds(200));
            peer.TransferStarting = () => hold.Task;

            Task<RawResponse> first = RawAsync(peer.Port, Get(PeerCache.PathPrefix + Sha(_package)));
            for (int i = 0; i < 500 && peer.ActiveClients < 1; i++) await Task.Delay(10);

            RawResponse busy = await RawAsync(peer.Port, Get(PeerCache.PathPrefix + Sha(_package)));
            Assert.Equal(503, busy.Status);
            Assert.Equal("30", busy.Headers["Retry-After"]);
            Assert.Empty(busy.Body);

            // İndiren ajan meşgul eşi atlayıp sıradaki kaynağa geçer
            Server server = Serving(_package);
            string path = Path.Combine(AgentUpdate.DataDir, MsiName);
            Assert.True(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(server), "https://pops.example/updates/" + MsiName, path, Artifact(), Update(),
                new[] { Peer("HW-000000000001", peer.Port, Sha(_package)) }));
            Assert.Equal(1, server.Requests);
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));

            hold.SetResult();
            Assert.Equal(_package, (await first).Body);
        }

        [Fact]
        public async Task Server_ClosesAClientThatSendsNothing()
        {
            string file = Path.Combine(TestEnvironment.NewDir("peer-remote"), Sha(_package));
            File.WriteAllBytes(file, _package);
            var peer = new PeerCacheServer(Sha(_package), file, new IPEndPoint(IPAddress.Loopback, 0), _ => true, 4, headerTimeout: TimeSpan.FromMilliseconds(300));
            peer.Start();
            _peers.Add(peer);

            using var idle = new TcpClient();
            await idle.ConnectAsync(IPAddress.Loopback, peer.Port);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int read = await idle.GetStream().ReadAsync(new byte[16], cts.Token);
            Assert.Equal(0, read);
        }

        [Fact]
        public async Task Isolated_DoesNotServe_AndServesAgainAfterwards()
        {
            await StoreAsync();
            _isolated = true;
            await PeerCache.SyncAsync();
            Assert.Null(PeerCache.ServingPort);
            Assert.DoesNotContain(Firewall(), s => s.Contains("New-NetFirewallRule", StringComparison.Ordinal));

            _isolated = false;
            await PeerCache.SyncAsync();
            Assert.NotNull(PeerCache.ServingPort);

            _isolated = true;
            await PeerCache.SyncAsync();
            Assert.Null(PeerCache.ServingPort);
            Assert.Contains("Remove-NetFirewallRule", Firewall().Last());
            Assert.True(File.Exists(CachePath())); // paket kalır, karantina bitince yeniden sunulur
        }

        // ---------------------------------------------------------------------------------------- güvenlik duvarı
        [Fact]
        public async Task Firewall_LocalSubnetRuleAdded_WhenServing_AndRemovedOnStop()
        {
            await StoreAsync();
            await PeerCache.SyncAsync();
            await PeerCache.SyncAsync(); // ikinci denetim kuralı yeniden eklemez

            string add = Assert.Single(Firewall());
            Assert.Contains("New-NetFirewallRule -Group $group", add);
            Assert.Contains("$group = 'POps Peer Cache'", add);
            Assert.Contains("-Direction Inbound -Action Allow -Protocol TCP -LocalPort 8817 -RemoteAddress LocalSubnet", add);
            Assert.Contains("-Program '" + Environment.ProcessPath.Replace("'", "''") + "'", add);
            Assert.DoesNotContain("Isolation", add);
            Assert.DoesNotContain("Set-NetFirewallProfile", add);

            await PeerCache.ShutdownAsync();
            Assert.Null(PeerCache.ServingPort);
            string remove = Firewall().Last();
            Assert.Equal(2, Firewall().Length);
            Assert.Contains("Get-NetFirewallRule -Group 'POps Peer Cache'", remove);
            Assert.Contains("Remove-NetFirewallRule", remove);
            Assert.DoesNotContain("New-NetFirewallRule", remove);
            Assert.True(File.Exists(CachePath())); // servis durunca dosya kalır, yeni sürüm sunar

            // Kapanıştan sonra kuyrukta kalan dakikalık denetim sunucuyu yeniden açmaz
            await PeerCache.SyncAsync();
            Assert.Null(PeerCache.ServingPort);
            Assert.Equal(2, Firewall().Length);
        }

        [Fact]
        public async Task Firewall_Failure_IsRetriedLater_ServingStillWorks()
        {
            int calls = 0;
            PeerCache.FirewallRunner = _ => { calls++; return Task.FromResult((1, "erişim engellendi")); };
            await StoreAsync();
            await PeerCache.SyncAsync();
            await PeerCache.SyncAsync();
            Assert.Equal(1, calls);
            Assert.NotNull(PeerCache.ServingPort);

            _now = _now.AddMinutes(16);
            await PeerCache.SyncAsync();
            Assert.Equal(2, calls);
        }

        [Fact]
        public void Firewall_ScriptsQuoteTheProgramPath()
        {
            string script = PeerCache.BuildAddRuleScript(@"C:\Program Files\PO'ps\POpsAgent.exe");
            Assert.Contains(@"-Program 'C:\Program Files\PO''ps\POpsAgent.exe'", script);
            Assert.Contains("-DisplayName 'POps Peer Cache (TCP 8817)'", script);
        }

        [Fact]
        public void Firewall_ScriptsParseWithoutErrors()
        {
            foreach (string script in new[] { PeerCache.BuildAddRuleScript(@"C:\Program Files\POps\POpsAgent.exe"), PeerCache.BuildRemoveRuleScript() })
            {
                string file = Path.Combine(TestEnvironment.NewDir("peer-script"), "rule.ps1");
                File.WriteAllText(file, script);
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", $"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{file}', [ref]$null, [ref]$e); $e.Count" })
                    psi.ArgumentList.Add(arg);
                using var p = System.Diagnostics.Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                Assert.Equal("0", output);
            }
        }

        // ---------------------------------------------------------------------------------------- eşlerden indirme
        [Fact]
        public async Task Download_FromAPeer_IsVerified_AndTheServerIsNotAsked()
        {
            PeerCacheServer peer = StartPeer(_package, Sha(_package));
            Server server = Serving(_package);
            string path = Path.Combine(AgentUpdate.DataDir, MsiName);

            Assert.True(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(server), "https://pops.example/updates/" + MsiName, path, Artifact(), Update(),
                new[] { Peer("HW-3F9A1C7B2E4D", peer.Port, Sha(_package)) }));
            Assert.Equal(0, server.Requests);
            Assert.Equal(_package, File.ReadAllBytes(path));
            Assert.Equal(new[] { "downloaded", "verified" }, Stages());
            Assert.Equal(AgentUpdate.SourcePeerPrefix + "HW-3F9A1C7B2E4D", Detail("downloaded"));
            Assert.False(File.Exists(path + ".peer"));
        }

        [Fact]
        public async Task Download_BadPeersAreSkipped_ThenTheServerIsUsed()
        {
            byte[] wrong = (byte[])_package.Clone();
            wrong[0] ^= 0xFF;
            PeerCacheServer liar = StartPeer(wrong, Sha(_package));                          // aynı boyut, başka bayt
            PeerCacheServer shortOne = StartPeer(_package.Take(5000).ToArray(), Sha(_package)); // kısa
            int closedPort;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            Server server = Serving(_package);
            string path = Path.Combine(AgentUpdate.DataDir, MsiName);

            Assert.True(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(server), "https://pops.example/updates/" + MsiName, path, Artifact(), Update(), new[]
            {
                Peer("HW-000000000001", closedPort, Sha(_package)),
                Peer("HW-000000000002", liar.Port, Sha(_package)),
                Peer("HW-000000000003", shortOne.Port, Sha(_package)),
            }));
            Assert.Equal(1, server.Requests);
            Assert.Equal(_package, File.ReadAllBytes(path));
            Assert.Equal(new[] { "downloaded", "verified" }, Stages());
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));
            Assert.False(File.Exists(path + ".peer"));
        }

        [Fact]
        public async Task Download_PeerThatNeverAnswers_IsLeftAfterTheHeaderTimeout()
        {
            PeerDownload.HeaderTimeout = TimeSpan.FromMilliseconds(300);
            int silent = RawPeer(_ => Task.Delay(TimeSpan.FromSeconds(5)));
            Server server = Serving(_package);
            string path = Path.Combine(AgentUpdate.DataDir, MsiName);

            Assert.True(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(server), "https://pops.example/updates/" + MsiName, path, Artifact(), Update(),
                new[] { Peer("HW-000000000001", silent, Sha(_package)) }));
            Assert.Equal(1, server.Requests);
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));
        }

        [Fact]
        public async Task Download_PeerThatStalls_IsLeftAfterTheStallTimeout()
        {
            PeerDownload.StallTimeout = TimeSpan.FromMilliseconds(300);
            int stalling = RawPeer(async stream =>
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {_package.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(_package.AsMemory(0, 1000));
                await Task.Delay(TimeSpan.FromSeconds(5));
            });
            Server server = Serving(_package);
            string path = Path.Combine(AgentUpdate.DataDir, MsiName);

            Assert.True(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(server), "https://pops.example/updates/" + MsiName, path, Artifact(), Update(),
                new[] { Peer("HW-000000000001", stalling, Sha(_package)) }));
            Assert.Equal(1, server.Requests);
            Assert.Equal(_package, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".peer"));
        }

        [Fact]
        public async Task Download_PeerWithoutTheExpectedContentLength_IsSkipped()
        {
            // Doğru baytlar, ama Content-Length yok (sözleşme: 200 ve beklenen Content-Length değilse sıradaki kaynak)
            int noLength = RawPeer(async stream =>
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(_package);
            });
            Server server = Serving(_package);
            string path = Path.Combine(AgentUpdate.DataDir, MsiName);

            Assert.True(await AgentUpdate.DownloadVerifiedAsync(new HttpClient(server), "https://pops.example/updates/" + MsiName, path, Artifact(), Update(),
                new[] { Peer("HW-000000000001", noLength, Sha(_package)) }));
            Assert.Equal(1, server.Requests);
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));
        }

        [Fact]
        public async Task UpdateCommand_WithPeers_DownloadsFromThePeer_AndCachesIt()
        {
            PeerDownload.AllowLoopbackPeers = true;
            PeerCacheServer peer = StartPeer(_package, Sha(_package));
            Server server = Serving(_package);
            await Run(Command(peers: new object[]
            {
                new { hw_id = "HW-3F9A1C7B2E4D", url = PeerUrl(peer.Port, Sha(_package)) },
            }), server);

            Assert.Equal(0, server.Requests);
            Assert.Equal(new[] { "received", "downloaded", "verified", "updater_started" }, Stages());
            Assert.Equal("peer HW-3F9A1C7B2E4D", Detail("downloaded"));
            Assert.Equal(_package, File.ReadAllBytes(CachePath()));
        }

        [Fact]
        public async Task UpdateCommand_PeersMustBeInTheLocalSubnet()
        {
            PeerDownload.AllowLoopbackPeers = true;
            PeerCache.IsLocalSubnet = _ => false;
            PeerCacheServer peer = StartPeer(_package, Sha(_package));
            Server server = Serving(_package);
            await Run(Command(peers: new object[] { new { hw_id = "HW-3F9A1C7B2E4D", url = PeerUrl(peer.Port, Sha(_package)) } }), server);

            Assert.Equal(1, server.Requests);
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));
        }

        [Fact]
        public async Task UpdateCommand_WhileIsolated_DoesNotAskPeers()
        {
            PeerDownload.AllowLoopbackPeers = true;
            _isolated = true;
            int asked = 0;
            PeerCacheServer peer = StartPeer(_package, Sha(_package), isLocal: _ => { Interlocked.Increment(ref asked); return true; });
            Server server = Serving(_package);
            await Run(Command(peers: new object[] { new { hw_id = "HW-3F9A1C7B2E4D", url = PeerUrl(peer.Port, Sha(_package)) } }), server);

            Assert.Equal(1, server.Requests);
            Assert.Equal(0, asked);
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));
        }

        // ---------------------------------------------------------------------------------------- sunucunun bayrağı
        [Fact]
        public async Task UpdateWithoutThePeerCacheFlag_KeepsNoPackage_OpensNoPort_AddsNoRule()
        {
            await Run(Command(peerCache: false), Serving(_package));

            Assert.Equal(new[] { "received", "downloaded", "verified", "updater_started" }, Stages());
            Assert.False(Directory.Exists(PeerCache.Dir));
            await PeerCache.SyncAsync(); // dakikalık denetim de bir şey açmaz
            Assert.Null(PeerCache.ServingPort);
            Assert.Equal(0, Volatile.Read(ref _listens));
            Assert.Empty(Firewall());
            Assert.False(Directory.Exists(PeerCache.Dir));
        }

        [Fact]
        public async Task UpdateWithoutThePeerCacheFlag_StillTriesThePeers()
        {
            PeerDownload.AllowLoopbackPeers = true;
            PeerCacheServer peer = StartPeer(_package, Sha(_package));
            Server server = Serving(_package);
            await Run(Command(peers: new object[] { new { hw_id = "HW-3F9A1C7B2E4D", url = PeerUrl(peer.Port, Sha(_package)) } }, peerCache: false), server);

            Assert.Equal(0, server.Requests);
            Assert.Equal("peer HW-3F9A1C7B2E4D", Detail("downloaded"));
            Assert.False(Directory.Exists(PeerCache.Dir));
            Assert.Equal(0, Volatile.Read(ref _listens));
            Assert.Empty(Firewall());
        }

        [Fact]
        public async Task UpdateWithoutThePeerCacheFlag_EmptiesAnEarlierCache_AndStopsServing()
        {
            await StoreAsync();
            await PeerCache.SyncAsync();
            int port = Assert.IsType<int>(PeerCache.ServingPort);

            // Aynı paket bile: sunucu tutulmasını istemiyor, önbellekten alınmaz, silinir
            Server server = Serving(_package);
            bool goneAtDownload = false;
            server.OnRequest = () => goneAtDownload = !File.Exists(CachePath()) && PeerCache.ServingPort == null;
            await Run(Command(peerCache: false), server);

            Assert.True(goneAtDownload);
            Assert.Equal(1, server.Requests);
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));
            Assert.True(await PortClosedAsync(port));
            Assert.Contains("Remove-NetFirewallRule", Firewall().Last());
            await PeerCache.SyncAsync();
            Assert.Null(PeerCache.ServingPort);
            Assert.False(Directory.Exists(PeerCache.Dir));
            Assert.Equal(1, Volatile.Read(ref _listens));
            Assert.Single(Firewall(), f => f.Contains("New-NetFirewallRule", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("{\"action\":\"update_agent\",\"peer_cache\":true}", true)]
        [InlineData("{\"action\":\"update_agent\"}", false)]
        [InlineData("{\"action\":\"update_agent\",\"peer_cache\":false}", false)]
        [InlineData("{\"action\":\"update_agent\",\"peer_cache\":\"true\"}", false)]
        [InlineData("{\"action\":\"update_agent\",\"peer_cache\":1}", false)]
        [InlineData("{\"action\":\"update_agent\",\"peer_cache\":null}", false)]
        [InlineData("[true]", false)]
        public void PeerCacheFlag_OnlyJsonTrueCounts(string json, bool expected) =>
            Assert.Equal(expected, PeerCache.Requested(JsonDocument.Parse(json).RootElement));

        [Fact]
        public void PeerCacheFlag_IsInTheProtocolVector()
        {
            JsonElement vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "docs", "protocol", "examples", "server-to-agent", "update_agent.peers.json"))).RootElement;
            Assert.True(PeerCache.Requested(vector));
            Assert.False(PeerCache.Requested(JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "docs", "protocol", "examples", "server-to-agent", "update_agent.json"))).RootElement));
        }

        // ---------------------------------------------------------------------------------------- sınav modu
        [Fact]
        public async Task ExamMode_StopsServing_SkipsPeers_AndServingResumesAfterwards()
        {
            PeerCache.IsIsolated = PeerCache.DefaultIsolated;
            await StoreAsync();
            await PeerCache.SyncAsync();
            Assert.NotNull(PeerCache.ServingPort);

            SecureStore.WriteProtected(ExamMode.StatePath, "{}");
            Assert.True(ExamMode.IsActive);
            await PeerCache.SyncAsync();
            Assert.Null(PeerCache.ServingPort);
            Assert.Contains("Remove-NetFirewallRule", Firewall().Last());

            // Sınav sürerken emirdeki eşlere gidilmez; yeni paket sunucudan iner ve önbelleğe alınır ama sunulmaz
            PeerDownload.AllowLoopbackPeers = true;
            byte[] next = RandomNumberGenerator.GetBytes(150_000);
            int asked = 0;
            PeerCacheServer peer = StartPeer(next, Sha(next), isLocal: _ => { Interlocked.Increment(ref asked); return true; });
            Server server = Serving(next);
            await Run(Command(next, peers: new object[] { new { hw_id = "HW-3F9A1C7B2E4D", url = PeerUrl(peer.Port, Sha(next)) } }), server);
            Assert.Equal(1, server.Requests);
            Assert.Equal(0, asked);
            Assert.True(File.Exists(CachePath(next)));
            await PeerCache.SyncAsync();
            Assert.Null(PeerCache.ServingPort);

            File.Delete(ExamMode.StatePath);
            await PeerCache.SyncAsync();
            Assert.Equal(Sha(next), PeerCache.ServingSha256);
        }

        public static IEnumerable<object[]> RefusedUrls()
        {
            string sha = new string('c', 64);
            return new[]
            {
                new object[] { $"http://8.8.8.8:8817/pops-cache/{sha}", "özel" },
                new object[] { $"http://100.64.0.10:8817/pops-cache/{sha}", "özel" },
                new object[] { $"http://[2001:db8::10]:8817/pops-cache/{sha}", "özel" },
                new object[] { $"https://pc-12.lab.example:8817/pops-cache/{sha}", "yalnızca http" },
                new object[] { $"https://192.168.1.20:8817/pops-cache/{sha}", "yalnızca http" },
                new object[] { $"http://pc-12:8817/pops-cache/{sha}", "IP adresi değil" },
                new object[] { $"ftp://192.168.1.20/pops-cache/{sha}", "yalnızca http" },
                new object[] { $"http://192.168.1.20:8817/updates/{sha}", "yol" },
                new object[] { $"http://192.168.1.20:8817/pops-cache/{new string('d', 64)}", "yol" },
                new object[] { $"http://192.168.1.20:8817/pops-cache/{sha.ToUpperInvariant()}", "yol" },
                new object[] { $"http://192.168.1.20:8817/pops-cache/{sha}/", "yol" },
                new object[] { $"http://192.168.1.20:8817/x/../pops-cache/{sha}x", "yol" },
                new object[] { $"http://192.168.1.20:8817/pops-cache/{sha}?a=1", "sorgu" },
                new object[] { $"http://192.168.1.20:8817/pops-cache/{sha}#x", "parça" },
                new object[] { $"http://user:pw@192.168.1.20:8817/pops-cache/{sha}", "kullanıcı" },
                new object[] { $"http://127.0.0.1:8817/pops-cache/{sha}", "geri döngü" },
                new object[] { $"http://[::1]:8817/pops-cache/{sha}", "geri döngü" },
                new object[] { $"/pops-cache/{sha}", "geçerli bir adres değil" },
                new object[] { "", "adres yok" },
                new object[] { null, "adres yok" },
            };
        }

        [Theory]
        [MemberData(nameof(RefusedUrls))]
        public void PeerUrl_Refused(string url, string reason)
        {
            Assert.False(PeerDownload.TryValidate(url, new string('c', 64), out Uri uri, out string why));
            Assert.Null(uri);
            Assert.Contains(reason, why);
        }

        [Theory]
        [InlineData("http://192.168.1.20:8817/pops-cache/{sha}")]
        [InlineData("http://10.4.0.7:8817/pops-cache/{sha}")]
        [InlineData("http://172.20.3.4:9000/pops-cache/{sha}")]
        [InlineData("http://169.254.10.10:8817/pops-cache/{sha}")]
        [InlineData("http://[fe80::1]:8817/pops-cache/{sha}")]
        [InlineData("http://[fd12:3456::7]:8817/pops-cache/{sha}")]
        public void PeerUrl_Accepted(string url)
        {
            string sha = new string('c', 64);
            Assert.True(PeerDownload.TryValidate(url.Replace("{sha}", sha), sha, out Uri uri, out _));
            Assert.Equal(PeerCache.PathPrefix + sha, uri.AbsolutePath);
        }

        [Fact]
        public void PeerUrl_OutsideTheLocalSubnet_IsRefused()
        {
            PeerCache.IsLocalSubnet = _ => false;
            string sha = new string('c', 64);
            Assert.False(PeerDownload.TryValidate($"http://192.168.1.20:8817/pops-cache/{sha}", sha, out _, out string why));
            Assert.Contains("yerel alt ağ", why);
        }

        [Fact]
        public void Peers_AtMostFive_InOrder_MalformedSkipped()
        {
            string sha = new string('c', 64);
            string Url(int i) => $"http://192.168.1.{i}:8817/pops-cache/{sha}";
            JsonElement command = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                action = "update_agent",
                peers = new object[]
                {
                    new { hw_id = "HW-000000000001", url = Url(1) },
                    "not an object",
                    new { hw_id = "HW-000000000003", url = "http://8.8.8.8:8817/pops-cache/" + sha },
                    new { hw_id = "bad id with spaces", url = Url(4) },
                    new { hw_id = "HW-000000000005", url = Url(5) },
                    new { hw_id = "HW-000000000006", url = Url(6) },
                    new { hw_id = "HW-000000000007", url = Url(7) },
                },
            })).RootElement;

            List<PeerDownload.Peer> peers = PeerDownload.Parse(command, sha);
            Assert.Equal(new[] { "HW-000000000001", "?", "HW-000000000005" }, peers.Select(p => p.HwId));
            Assert.Equal(new[] { 1, 4, 5 }, peers.Select(p => int.Parse(p.Url.Host.Split('.')[3])));

            Assert.Empty(PeerDownload.Parse(JsonDocument.Parse("{\"action\":\"update_agent\"}").RootElement, sha));
            Assert.Empty(PeerDownload.Parse(JsonDocument.Parse("{\"peers\":\"192.168.1.1\"}").RootElement, sha));
        }

        [Theory]
        [InlineData("192.168.1.10", 24, "192.168.1.200", true)]
        [InlineData("192.168.1.10", 24, "192.168.2.1", false)]
        [InlineData("10.20.30.40", 20, "10.20.31.1", true)]
        [InlineData("10.20.30.40", 20, "10.20.48.1", false)]
        [InlineData("192.168.1.10", 24, "::ffff:192.168.1.7", true)]
        [InlineData("192.168.1.10", 0, "8.8.8.8", false)]
        [InlineData("fe80::1", 64, "fe80::abcd", true)]
        [InlineData("2001:db8::1", 32, "2001:db8:ffff::1", false)]
        [InlineData("192.168.1.10", 24, "fe80::1", false)]
        public void SameSubnet(string network, int prefix, string address, bool expected) =>
            Assert.Equal(expected, PeerCache.SameSubnet(IPAddress.Parse(network), prefix, IPAddress.Parse(address)));

        // ---------------------------------------------------------------------------------------- yetenek
        [Fact]
        public async Task CapabilityOff_DisablesServingAndPeerDownloads()
        {
            await StoreAsync();
            await PeerCache.SyncAsync();
            Assert.NotNull(PeerCache.ServingPort);

            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":true,\"vision_enabled\":true,\"peer_cache_enabled\":false}");
            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.PeerCacheEnabled);
            Assert.True(AgentCapabilities.TerminalEnabled);

            // Sunma: önbellek silinir, sunucu durur
            await PeerCache.SyncAsync();
            Assert.Null(PeerCache.ServingPort);
            Assert.False(File.Exists(CachePath()));

            // İndirme: eşlere gidilmez, doğrulanan paket önbelleğe alınmaz
            PeerDownload.AllowLoopbackPeers = true;
            int asked = 0;
            PeerCacheServer peer = StartPeer(_package, Sha(_package), isLocal: _ => { Interlocked.Increment(ref asked); return true; });
            Server server = Serving(_package);
            await Run(Command(peers: new object[] { new { hw_id = "HW-3F9A1C7B2E4D", url = PeerUrl(peer.Port, Sha(_package)) } }), server);
            Assert.Equal(1, server.Requests);
            Assert.Equal(0, asked);
            Assert.Equal(AgentUpdate.SourceServer, Detail("downloaded"));
            Assert.False(File.Exists(CachePath()));
            Assert.False(await PeerCache.StoreAsync(Path.Combine(AgentUpdate.UpdatesDir, MsiName), Sha(_package), _package.Length));
        }

        [Fact]
        public void Capability_ServerCanOnlySwitchItOff_AndItIsReported()
        {
            AgentCapabilities.Load();
            var (disabled, _) = AgentCapabilities.ApplyServerRequest(JsonDocument.Parse("{\"action\":\"set_capabilities\",\"peer_cache_enabled\":false}").RootElement);
            Assert.Equal(new[] { AgentCapabilities.PeerCacheKey }, disabled);
            Assert.False(AgentCapabilities.PeerCacheEnabled);

            var (_, ignored) = AgentCapabilities.ApplyServerRequest(JsonDocument.Parse("{\"peer_cache_enabled\":true}").RootElement);
            Assert.Equal(new[] { AgentCapabilities.PeerCacheKey }, ignored);
            Assert.False(AgentCapabilities.PeerCacheEnabled);

            // Kalıcı; "capabilities" mesajı peer_cache_enabled'ı da taşır (şemada isteğe bağlı)
            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.PeerCacheEnabled);
            Assert.Equal(false, AgentCapabilities.StatusMessage()[AgentCapabilities.PeerCacheKey]);
        }

        [Fact]
        public void Capability_UnreadableFile_SwitchesPeerCacheOff()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{ broken");
            AgentCapabilities.Load();
            Assert.False(AgentCapabilities.PeerCacheEnabled);
        }

        [Fact]
        public void Capability_OlderFileWithoutTheKey_KeepsPeerCacheOn()
        {
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"terminal_enabled\":false,\"vision_enabled\":true}");
            AgentCapabilities.Load();
            Assert.True(AgentCapabilities.PeerCacheEnabled);
            Assert.False(AgentCapabilities.TerminalEnabled);
        }

        // ---------------------------------------------------------------------------------------- duyuru
        [Fact]
        public void AgentFeatures_AnnouncePeerCache_OnlyWhileTheCapabilityIsOn()
        {
            Assert.Equal("X-Agent-Features", AgentFeatures.HeaderName);
            Assert.Contains("peer_cache", AgentFeatures.All);
            Assert.Matches("^[a-z0-9_]+(,[a-z0-9_]+)*$", AgentFeatures.Header);
            Assert.Contains("peer_cache", AgentFeatures.Header.Split(','));

            // Kapalı yetenekle duyurulmaz: sunucu bu PC'yi tohum ya da eş seçmesin
            SecureStore.WriteProtected(SecureStore.PathOf(AgentCapabilities.FileName), "{\"peer_cache_enabled\":false}");
            AgentCapabilities.Load();
            Assert.DoesNotContain("peer_cache", AgentFeatures.Header.Split(','));
            Assert.Contains("power", AgentFeatures.Header.Split(','));
            Assert.Matches("^[a-z0-9_]+(,[a-z0-9_]+)*$", AgentFeatures.Header);

            // Komut bağlantısı başlığı X-Agent-Version'dan hemen sonra ekler
            string worker = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "Agent", "POps.Agent", "POps.Agent", "Worker.cs"));
            Assert.Matches(new Regex(@"SetRequestHeader\(""X-Agent-Version"", AppVersion\);\r?\n\s*_commandWs\.Options\.SetRequestHeader\(AgentFeatures\.HeaderName, AgentFeatures\.Header\);"), worker);
        }
    }
}
