using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Eş önbelleğinin yerel alt ağ kuralı (bkz. PeerCache.InLocalSubnet): eşten indirme ve eşe sunma yalnızca bu bilgisayarın
    // kendi alt ağlarından birindeki adreslerle; adresin özel ya da genel olması fark etmez (genel IPv4 kullanan laboratuvarlar).
    // Arabirimler makine kapısıdır (MachineSeams.LocalSubnets): test kendi listesini verir, bu bilgisayarın ağına bakılmaz.
    // Sunucu hiç dinlemeyen bir sahtedir: soket açılmaz, paralel çalışır.
    public class PeerSubnetTests : TestBase, IDisposable
    {
        private static readonly string Sha = new string('c', 64);

        // Genel IPv4 kullanan bir laboratuvar (203.0.113.0/24), özel adresli ikinci bir arabirim (10.20.0.0/16), IPv6 bağlantı-
        // yerel ve arabirimdeki genel önek
        private readonly List<InterfaceSubnet> _subnets = new List<InterfaceSubnet>
        {
            new InterfaceSubnet(IPAddress.Parse("203.0.113.45"), 24),
            new InterfaceSubnet(IPAddress.Parse("10.20.0.5"), 16),
            new InterfaceSubnet(IPAddress.Parse("fe80::1234:5678"), 64),
            new InterfaceSubnet(IPAddress.Parse("2001:db8:10::45"), 64),
        };
        private readonly AgentHarness _agent;
        // Eş önbelleği sunucusuna verilen "yerel mi" denetimi (sahte sunucunun kurulduğu her sefer)
        private readonly List<Func<IPAddress, bool>> _served = new List<Func<IPAddress, bool>>();

        public PeerSubnetTests()
        {
            _agent = AgentHarness.Create("peer-subnet", machine: new MachineSeams
            {
                Kiosk = new FakeKioskRegistry(),
                LocalSubnets = () => _subnets,
                PeerCacheServer = (sha256, path, isLocal, maxClients) =>
                {
                    lock (_served) _served.Add(isLocal);
                    return new FakeServer(sha256);
                },
            });
            _agent.Capabilities.Load();   // dosya yok: hepsi açık
        }

        public void Dispose() => _agent.Dispose();

        private PeerCache PeerCache => _agent.Updater.PeerCache;
        private PeerDownload PeerDownload => _agent.Updater.PeerDownload;

        // Dinlemeyen sunucu: Start bir şey açmaz
        private sealed class FakeServer : IPeerCacheServer
        {
            public FakeServer(string sha256) => Sha256 = sha256;
            public string Sha256 { get; }
            public int Port => 0;
            public int ActiveClients => 0;
            public void Start() { }
            public Task StopAsync() => Task.CompletedTask;
            public void Dispose() { }
        }

        private static string Url(string host) => $"http://{host}:8817/pops-cache/{Sha}";

        private (bool Accepted, string Reason) Validate(string host)
        {
            bool accepted = PeerDownload.TryValidate(Url(host), Sha, out Uri uri, out string reason);
            Assert.Equal(accepted, uri != null);
            return (accepted, reason);
        }

        [Theory]
        [InlineData("203.0.113.20")]       // genel IPv4, bu bilgisayarın alt ağında
        [InlineData("203.0.113.1")]
        [InlineData("203.0.113.254")]
        [InlineData("10.20.3.4")]          // özel, bu bilgisayarın öteki alt ağında
        [InlineData("[fe80::abcd]")]       // IPv6 bağlantı-yerel
        [InlineData("[2001:db8:10::99]")]  // arabirimdeki IPv6 öneki
        [InlineData("[::ffff:203.0.113.20]")]
        public void Peer_OnThisPcsSubnet_IsAccepted(string host) => Assert.Equal((true, ""), Validate(host));

        // Yeni kural: özel adresli ama bu bilgisayarın alt ağlarında olmayan eş de reddedilir (eskiden de: kural "özel VE yerel
        // alt ağ" idi; şimdi yalnızca "yerel alt ağ")
        [Theory]
        [InlineData("198.51.100.7")]       // genel, başka alt ağ
        [InlineData("203.0.114.1")]        // komşu /24
        [InlineData("8.8.8.8")]
        [InlineData("192.168.1.20")]       // özel, başka alt ağ
        [InlineData("10.21.0.1")]
        [InlineData("172.16.0.10")]
        [InlineData("169.254.10.10")]      // IPv4 bağlantı-yerel, bu bilgisayarda öyle bir arabirim yok
        [InlineData("100.64.0.10")]
        [InlineData("[2001:db8:11::1]")]
        [InlineData("[fd12:3456::7]")]     // ULA, arabirimde yok
        public void Peer_OutsideThisPcsSubnets_IsRefused(string host)
        {
            var (accepted, reason) = Validate(host);
            Assert.False(accepted);
            Assert.Equal("bu bilgisayarın yerel alt ağında değil", reason);
        }

        // Alt ağın içinde olsa da eş olamayan adresler (çoklu yayın, yayın, belirtilmemiş, geri döngü); IPv4 ağının adresi ve
        // yayın adresi konak değildir
        [Theory]
        [InlineData("203.0.113.0", "yerel alt ağ")]
        [InlineData("203.0.113.255", "yerel alt ağ")]
        [InlineData("10.20.255.255", "yerel alt ağ")]
        [InlineData("224.0.0.251", "çoklu yayın")]
        [InlineData("[ff02::1]", "çoklu yayın")]
        [InlineData("255.255.255.255", "yayın")]
        [InlineData("0.0.0.0", "belirtilmemiş")]
        [InlineData("[::]", "belirtilmemiş")]
        [InlineData("127.0.0.1", "geri döngü")]
        [InlineData("[::1]", "geri döngü")]
        public void SpecialAddresses_AreNeverPeers(string host, string reason)
        {
            _subnets.Add(new InterfaceSubnet(IPAddress.Parse("127.0.0.1"), 8));
            _subnets.Add(new InterfaceSubnet(IPAddress.Parse("224.0.0.1"), 8));
            var (accepted, why) = Validate(host);
            Assert.False(accepted);
            Assert.Contains(reason, why);
        }

        [Fact]
        public void HostRule_FollowsTheInterfaceMask()
        {
            var subnets = new[]
            {
                new InterfaceSubnet(IPAddress.Parse("198.51.100.9"), 30),    // .8 ağ, .11 yayın
                new InterfaceSubnet(IPAddress.Parse("192.0.2.0"), 31),       // RFC 3021: iki adres de konak
                new InterfaceSubnet(IPAddress.Parse("192.0.2.10"), 32),
                new InterfaceSubnet(IPAddress.Parse("100.64.0.1"), 7),       // /8'den geniş: yerel sayılmaz
            };
            bool Local(string address) => PeerCache.InLocalSubnet(IPAddress.Parse(address), subnets);
            Assert.Equal(new[] { false, true, true, false }, new[] { "198.51.100.8", "198.51.100.9", "198.51.100.10", "198.51.100.11" }.Select(Local));
            Assert.True(Local("192.0.2.0"));
            Assert.True(Local("192.0.2.1"));
            Assert.True(Local("192.0.2.10"));
            Assert.False(Local("192.0.2.11"));
            Assert.False(Local("101.1.2.3"));
            Assert.False(PeerCache.InLocalSubnet(IPAddress.Parse("203.0.113.20"), Array.Empty<InterfaceSubnet>()));
        }

        // Arabirimler her denetimde yeniden sorulur (adres değişebilir)
        [Fact]
        public void Interfaces_AreAskedEachTime()
        {
            Assert.False(Validate("198.51.100.7").Accepted);
            _subnets.Add(new InterfaceSubnet(IPAddress.Parse("198.51.100.1"), 24));
            Assert.True(Validate("198.51.100.7").Accepted);
        }

        // Testlerin varsayılan makinesi bu bilgisayarın arabirimlerini okumaz: hiçbir adres yerel değil
        [Fact]
        public void RefusingMachine_HasNoLocalSubnet()
        {
            using var agent = AgentHarness.Create("peer-subnet-none");
            Assert.False(agent.Updater.PeerCache.IsLocalSubnet(IPAddress.Parse("192.168.1.20")));
            Assert.False(agent.Updater.PeerDownload.TryValidate(Url("192.168.1.20"), Sha, out _, out string reason));
            Assert.Equal("bu bilgisayarın yerel alt ağında değil", reason);
        }

        // ------------------------------------------------------------------ log
        private static List<string> LogLines(string marker)
        {
            string path = POpsHelpers.LogFilePath(DateTime.Now);
            if (!File.Exists(path)) return new List<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').Where(line => line.Contains(marker, StringComparison.Ordinal)).ToList();
        }

        private static JsonElement Command(params (string HwId, string Host)[] peers) =>
            JsonSerializer.SerializeToElement(new { action = "update_agent", peers = peers.Select(p => new { hw_id = p.HwId, url = Url(p.Host) }).ToArray() });

        // Alt ağ dışındaki eş: ilk ret bir uyarı, sonrakiler (aynı ya da sonraki emirde) yalnızca bilgi satırı. Başka bir
        // nedenle reddedilen eş eskisi gibi her seferinde [GÜVENLİK] uyarısıdır.
        [Fact]
        public void RefusedOutsideTheSubnets_WarnsOnce()
        {
            string run = Guid.NewGuid().ToString("N").Substring(0, 12);
            string first = "HW-A" + run, second = "HW-B" + run, later = "HW-C" + run, https = "HW-D" + run;

            Assert.Empty(PeerDownload.Parse(Command((first, "198.51.100.7"), (second, "192.168.1.20")), Sha));
            JsonElement next = JsonSerializer.SerializeToElement(new
            {
                action = "update_agent",
                peers = new object[]
                {
                    new { hw_id = later, url = Url("198.51.100.8") },
                    new { hw_id = https, url = $"https://203.0.113.20:8817/pops-cache/{Sha}" },
                },
            });
            Assert.Empty(PeerDownload.Parse(next, Sha));

            List<string> lines = LogLines(run);
            string warning = Assert.Single(lines, line => line.Contains("[ERROR]", StringComparison.Ordinal) && !line.Contains("[GÜVENLİK]", StringComparison.Ordinal));
            Assert.Contains(first, warning);
            Assert.Contains("alt ağlarından hiçbirinde değil", warning);
            Assert.Contains("bir kez", warning);
            foreach (string label in new[] { second, later })
            {
                string line = Assert.Single(lines, l => l.Contains(label, StringComparison.Ordinal));
                Assert.Contains("[INFO ]", line);
                Assert.Contains("bu bilgisayarın yerel alt ağında değil", line);
            }
            string security = Assert.Single(lines, l => l.Contains(https, StringComparison.Ordinal));
            Assert.Contains("[ERROR]", security);
            Assert.Contains("[GÜVENLİK]", security);
        }

        // ------------------------------------------------------------------ sunum
        // Sunucu (PeerCacheServer.Decide) bağlanan adresi aynı kuralla denetler: bu bilgisayarın alt ağındaki genel adres
        // yanıt alır, başka alt ağdaki (özel de olsa) 403 alır
        [Fact]
        public async Task ServingSide_AnswersOnlyThisPcsSubnets()
        {
            byte[] package = RandomNumberGenerator.GetBytes(1000);
            string sha = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            Directory.CreateDirectory(PeerCache.Dir);
            File.WriteAllBytes(Path.Combine(PeerCache.Dir, sha), package);
            try
            {
                await PeerCache.SyncAsync();
                Assert.Equal(sha, PeerCache.ServingSha256);
                Func<IPAddress, bool> isLocal;
                lock (_served) isLocal = Assert.Single(_served);

                Assert.True(isLocal(IPAddress.Parse("203.0.113.20")));
                Assert.True(isLocal(IPAddress.Parse("10.20.3.4")));
                Assert.True(isLocal(IPAddress.Parse("fe80::abcd")));
                Assert.False(isLocal(IPAddress.Parse("198.51.100.7")));
                Assert.False(isLocal(IPAddress.Parse("192.168.1.20")));
                _subnets.Add(new InterfaceSubnet(IPAddress.Parse("198.51.100.1"), 24));
                Assert.True(isLocal(IPAddress.Parse("198.51.100.7")));
            }
            finally { await PeerCache.ShutdownAsync(); }
        }
    }
}
