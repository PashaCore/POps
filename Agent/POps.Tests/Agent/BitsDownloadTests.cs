using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Güncelleme paketinin BITS ile indirilmesi: betik, sonuçlar ve eski yola düşme; imzalı SHA-256 denetimi iki yolda da
    public class BitsDownloadTests : TestBase, IDisposable
    {
        private readonly Func<string, TimeSpan, Task<(int, string)>> _runner = BitsDownload.Runner;
        private readonly byte[] _package = RandomNumberGenerator.GetBytes(3000);
        private readonly List<string> _stages = new List<string>();
        private int _httpRequests;

        public BitsDownloadTests()
        {
            AgentUpdate.DataDir = TestEnvironment.NewDir("bits-data");
            BitsDownload.Enabled = true;
        }

        public void Dispose()
        {
            BitsDownload.Enabled = false;
            BitsDownload.Runner = _runner;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
        }

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

        private Task<bool> Download(string path) => AgentUpdate.DownloadVerifiedAsync(new HttpClient(new Server(this)), "https://pops.example/updates/x.msi", path,
            Artifact(), new AgentUpdate.UpdateCommand { Report = m => { lock (_stages) _stages.Add((string)m["stage"]); return Task.FromResult(true); } });

        // Sahte BITS: hedefe verilen baytları yazar ve çıkış kodunu döner
        private void Bits(int exit, byte[] content = null) => BitsDownload.Runner = (script, _) =>
        {
            if (content != null)
            {
                string target = script.Split('\n').First(l => l.StartsWith("$target = ", StringComparison.Ordinal)).Substring("$target = ".Length).Trim().Trim('\'').Replace("''", "'");
                File.WriteAllBytes(target, content);
            }
            return Task.FromResult((exit, exit == 0 ? "OK" : exit == 3 ? "BITS: süre doldu" : "BITS: hata"));
        };

        [Fact]
        public async Task Bits_Done_IsVerifiedLikeADirectDownload()
        {
            Bits(0, _package);
            string path = Path.Combine(AgentUpdate.DataDir, "x.msi");
            Assert.True(await Download(path));
            Assert.Equal(_package, File.ReadAllBytes(path));
            Assert.Equal(0, _httpRequests);
            Assert.Equal(new[] { "downloaded", "verified" }, _stages);
            Assert.False(File.Exists(path + ".bits"));
        }

        [Fact]
        public async Task Bits_WrongContent_IsRejected()
        {
            Bits(0, _package.Take(100).ToArray());
            string path = Path.Combine(AgentUpdate.DataDir, "x.msi");
            Assert.False(await Download(path));
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".bits"));
            Assert.Equal(new[] { "downloaded" }, _stages);
        }

        [Fact]
        public async Task Bits_StillRunning_RejectsWithoutASecondDownload()
        {
            Bits(3);
            Assert.False(await Download(Path.Combine(AgentUpdate.DataDir, "x.msi")));
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
            string path = Path.Combine(AgentUpdate.DataDir, "x.msi");
            Assert.True(await Download(path));
            Assert.Equal(1, _httpRequests);
            Assert.Equal(_package, File.ReadAllBytes(path));
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
