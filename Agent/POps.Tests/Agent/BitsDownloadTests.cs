using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Güncelleme paketinin BITS ile indirilmesi: betik, sonuçlar ve eski yola düşme; imzalı SHA-256 denetimi iki yolda da
    // Machine: betiğin ayrıştırılması için powershell.exe başlatılır (Script_ParsesWithoutErrors)
    [Collection(MachineCollection.Name)]
    public class BitsDownloadTests : TestBase, IDisposable
    {
        // Testin ajanı: BITS kapısı testin sahte çalıştırıcısıdır (bkz. Bits)
        private readonly AgentHarness _agent;
        private Func<string, TimeSpan, Task<(int, string)>> _runner;
        private readonly byte[] _package = RandomNumberGenerator.GetBytes(3000);
        private readonly List<string> _stages = new List<string>();
        // "downloaded" aşamasının detail'i (paketin kaynağı)
        private readonly List<string> _sources = new List<string>();
        private int _httpRequests;
        // BITS kapısının kaç kez çağrıldığı
        private int _bitsCalls;

        public BitsDownloadTests()
        {
            _agent = AgentHarness.Create("bits-data", machine: new MachineSeams
            {
                Kiosk = new FakeKioskRegistry(),
                Bits = (script, timeout) =>
                {
                    Interlocked.Increment(ref _bitsCalls);
                    return _runner(script, timeout);
                },
            });
        }

        public void Dispose() => _agent.Dispose();

        private ReleaseVerifier.Artifact Artifact() => new ReleaseVerifier.Artifact
        {
            Name = "POps-Agent-9.9.9-win-x64.msi",
            Size = _package.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(_package)).ToLowerInvariant(),
        };

        private sealed class Server : HttpMessageHandler
        {
            private readonly BitsDownloadTests _t;
            public Server(BitsDownloadTests t) { _t = t; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _t._httpRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_t._package) });
            }
        }

        private Task<bool> Download(string path) => _agent.Updater.DownloadVerifiedAsync(new HttpClient(new Server(this)), "https://pops.example/updates/x.msi", path,
            Artifact(), new AgentUpdate.UpdateCommand
            {
                Report = m =>
                {
                    lock (_stages)
                    {
                        _stages.Add((string)m["stage"]);
                        if ((string)m["stage"] == "downloaded") _sources.Add(m.TryGetValue("detail", out object detail) ? (string)detail : null);
                    }
                    return Task.FromResult(true);
                },
            });

        // Bugünkü log dosyasında bu satırı içeren satır sayısı (BITS'i yalnızca bu sınıf kullanır: başka bir test "(BITS)" yazmaz)
        private static int LogCount(string text)
        {
            string path = POpsHelpers.LogFilePath(DateTime.Now);
            if (!File.Exists(path)) return 0;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').Count(line => line.Contains(text, StringComparison.Ordinal));
        }

        private const string BitsLog = "Paket indirildi (BITS).";

        // Sahte BITS: hedefe verilen baytları yazar ve çıkış kodunu döner
        private void Bits(int exit, byte[] content = null) => _runner = (script, _) =>
        {
            if (content != null)
            {
                string target = script.Split('\n').First(l => l.StartsWith("$target = ", StringComparison.Ordinal)).Substring("$target = ".Length).Trim().Trim('\'').Replace("''", "'");
                File.WriteAllBytes(target, content);
            }
            return Task.FromResult((exit, exit == 0 ? "OK" : exit == 3 ? "BITS: süre doldu" : "BITS: hata"));
        };

        // BITS'le inen paket doğrudan indirilen gibi denetlenir; log ve "downloaded" kaynağı BITS der (saha: 0.1.24-alpha paketi
        // BITS'le indi, log yalnızca "Paket indirildi (server)." diyordu)
        [Fact]
        public async Task Bits_Done_IsVerifiedLikeADirectDownload_AndSaysBits()
        {
            Bits(0, _package);
            string path = Path.Combine(_agent.Paths.DataDir, "x.msi");
            int logged = LogCount(BitsLog);
            Assert.True(await Download(path));
            Assert.Equal(_package, File.ReadAllBytes(path));
            Assert.Equal(0, _httpRequests);
            Assert.Equal(1, _bitsCalls);
            Assert.Equal(new[] { "downloaded", "verified" }, _stages);
            Assert.Equal(new[] { "bits" }, _sources);
            Assert.Equal(AgentUpdate.SourceBits, _sources[0]);
            Assert.Equal(logged + 1, LogCount(BitsLog));
            Assert.False(File.Exists(path + ".bits"));
        }

        // Sunucu sertifikası kurum sertifikasına sabitliyken (server-ca.pem, "server_ca": "custom") BITS hiç çağrılmaz: BITS
        // Windows'un güven deposunu kullanır, kurum sertifikasını kullanamaz. Paket doğrudan (ajanın HttpClient'ıyla) iner ve
        // aynı imzalı manifest denetiminden geçer.
        [Fact]
        public async Task PinnedServerCa_SkipsBits_AndDownloadsDirectly()
        {
            using (ECDsa key = ECDsa.Create())
            {
                var request = new CertificateRequest("CN=POps Test CA", key, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                using X509Certificate2 ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
                File.WriteAllText(_agent.Paths.ServerCaPath, ca.ExportCertificatePem());
            }
            _agent.Trust.Reload();
            Assert.Equal(ServerTrust.ModeCustom, _agent.Capabilities.ServerCa);
            Bits(0, _package);

            string path = Path.Combine(_agent.Paths.DataDir, "x.msi");
            Assert.True(await Download(path));
            Assert.Equal(0, _bitsCalls);
            Assert.Equal(1, _httpRequests);
            Assert.Equal(_package, File.ReadAllBytes(path));
            Assert.Equal(new[] { "downloaded", "verified" }, _stages);
            Assert.Equal(new[] { AgentUpdate.SourceServer }, _sources);
            Assert.False(File.Exists(path + ".bits"));
        }

        // Bozuk server-ca.pem de "custom"dır (bağlantılar kapalı yöne düşer): BITS sistem deposuyla sabitlemeyi aşmaz
        [Fact]
        public async Task BrokenServerCa_SkipsBitsToo()
        {
            File.WriteAllText(_agent.Paths.ServerCaPath, "sertifika değil");
            _agent.Trust.Reload();
            Bits(0, _package);
            Assert.True(await Download(Path.Combine(_agent.Paths.DataDir, "x.msi")));
            Assert.Equal(0, _bitsCalls);
            Assert.Equal(1, _httpRequests);
        }

        [Fact]
        public async Task Bits_WrongContent_IsRejected()
        {
            Bits(0, _package.Take(100).ToArray());
            string path = Path.Combine(_agent.Paths.DataDir, "x.msi");
            Assert.False(await Download(path));
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".bits"));
            Assert.Equal(new[] { "downloaded" }, _stages);
            Assert.Equal(new[] { AgentUpdate.SourceBits }, _sources);
        }

        [Fact]
        public async Task Bits_StillRunning_RejectsWithoutASecondDownload()
        {
            Bits(3);
            Assert.False(await Download(Path.Combine(_agent.Paths.DataDir, "x.msi")));
            Assert.Equal(0, _httpRequests);
            Assert.Empty(_stages);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(1)]
        [InlineData(-1)]
        public async Task Bits_Unavailable_FallsBackToHttpClient(int exit)
        {
            Bits(exit);
            string path = Path.Combine(_agent.Paths.DataDir, "x.msi");
            int logged = LogCount(BitsLog);
            Assert.True(await Download(path));
            Assert.Equal(1, _httpRequests);
            Assert.Equal(_package, File.ReadAllBytes(path));
            // Doğrudan indirildi: kaynak sunucu, log "(server)"; BITS satırı yok
            Assert.Equal(new[] { "downloaded", "verified" }, _stages);
            Assert.Equal(new[] { AgentUpdate.SourceServer }, _sources);
            Assert.Equal(logged, LogCount(BitsLog));
        }

        [Fact]
        public void Script_QuotesEveryValue()
        {
            string script = BitsDownload.BuildScript("https://pops.example/updates/a'; Remove-Item C:\\ #.msi", @"C:\POpsData\updates\x.msi.bits", "POps update abc", TimeSpan.FromMinutes(15));
            Assert.Contains("$source = 'https://pops.example/updates/a''; Remove-Item C:\\ #.msi'", script);
            Assert.Contains("$target = 'C:\\POpsData\\updates\\x.msi.bits'", script);
            Assert.Contains("$name = 'POps update abc'", script);
            Assert.Contains("AddSeconds(900)", script);
            Assert.Equal("'a''b'", BitsDownload.Quote("a'b"));
            // PowerShell'in kapatan öbür tırnakları da ikilenir: dizgiden çıkılamaz
            Assert.Equal("'a\u2019\u2019; calc; \u2018\u2018b'", BitsDownload.Quote("a\u2019; calc; \u2018b"));
            Assert.Equal("'\u201A\u201A\u201B\u201B'", BitsDownload.Quote("\u201A\u201B"));
            Assert.Equal("''", BitsDownload.Quote(null));
            Assert.Equal("POps update 0123456789abcdef", BitsDownload.JobName("0123456789abcdef" + new string('0', 48)));
        }

        [Fact]
        public void Script_ParsesWithoutErrors()
        {
            string file = Path.Combine(TestEnvironment.NewDir("bits-script"), "bits.ps1");
            File.WriteAllText(file, BitsDownload.BuildScript("https://pops.example/updates/x.msi", @"C:\x.msi.bits", "POps update abc", TimeSpan.FromMinutes(15)));
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", $"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{file}', [ref]$null, [ref]$e); $e.Count" })
                psi.ArgumentList.Add(arg);
            using Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            Assert.Equal("0", output);
        }
    }
}
