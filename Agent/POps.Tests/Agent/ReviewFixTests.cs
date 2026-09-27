using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Tepsi ve servis aynı bypass kodu biçimini kullanır; biçim hatası servise gitmez (L3)
    public class BypassCodeTests : TestBase
    {
        [Theory]
        [InlineData("372cc1", "372CC1")]
        [InlineData(" 372CC1 ", "372CC1")]
        [InlineData("0123456789abcdef", "0123456789ABCDEF")]
        public void WellFormedCodes_AreNormalized(string code, string expected) => Assert.Equal(expected, BypassCode.Normalize(code));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("O72CC1")]   // O harfi, 0 değil
        [InlineData("12345")]
        [InlineData("372 CC1")]
        [InlineData("GHIJKL")]
        public void MalformedCodes_AreRejected(string code) => Assert.False(BypassCode.IsWellFormed(code));

        [Fact]
        public void LengthLimits()
        {
            Assert.True(BypassCode.IsWellFormed(new string('A', 64)));
            Assert.False(BypassCode.IsWellFormed(new string('A', 65)));
        }
    }

    // Sunucudan gelen metin loga ham yazılmaz (L7)
    public class LogTextTests : TestBase
    {
        [Fact]
        public void LineBreaksAndControlCharacters_AreReplaced()
        {
            // Kültüre duyarlı karşılaştırmada denetim karakterleri "yok sayılır" ve her yerde bulunmuş görünür: sıralı karşılaştırma
            string safe = LogText.Safe("security\r\n2026-09-28 [AGENT] sahte satır\u2028\u0007", 200);
            Assert.DoesNotContain("\r", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2028", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("\u0007", safe, StringComparison.Ordinal);
            Assert.StartsWith("security??2026", safe);
        }

        [Fact]
        public void LongText_IsShortened()
        {
            Assert.Equal("abcde…", LogText.Safe("abcdefghij", 5));
            Assert.Equal("all", LogText.Safe("all", 5));
            Assert.Equal("(yok)", LogText.Safe(null));
        }
    }

    // AgentHttp: yönlendirme izlenmez (L10), 404/405 "uç yok" sayılır (M2). Gerçek bir TCP dinleyicisiyle, yalnızca
    // 127.0.0.1 üzerinde denenir.
    public class AgentHttpTests : TestBase, IDisposable
    {
        private sealed class FakeHttpServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly string _response;
            private int _requests;
            public string LastRequest { get; private set; }
            public int Requests => Volatile.Read(ref _requests);

            public FakeHttpServer(string response)
            {
                _response = response;
                _listener.Start();
                _ = Task.Run(AcceptLoopAsync);
            }

            public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

            private async Task AcceptLoopAsync()
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        var buffer = new byte[256 * 1024];
                        int total = 0;
                        while (true)
                        {
                            int n = await stream.ReadAsync(buffer, total, buffer.Length - total);
                            if (n == 0) break;
                            total += n;
                            string text = Encoding.UTF8.GetString(buffer, 0, total);
                            int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (headerEnd < 0) continue;
                            Match length = Regex.Match(text, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                            if (total >= headerEnd + 4 + (length.Success ? int.Parse(length.Groups[1].Value) : 0)) break;
                        }
                        LastRequest = Encoding.UTF8.GetString(buffer, 0, total);
                        Interlocked.Increment(ref _requests);
                        byte[] response = Encoding.ASCII.GetBytes(_response);
                        await stream.WriteAsync(response, 0, response.Length);
                    }
                }
            }

            public void Dispose() => _listener.Stop();
        }

        private static string Status(int code, string reason, string extraHeaders = "") =>
            $"HTTP/1.1 {code} {reason}\r\n{extraHeaders}Content-Length: 0\r\nConnection: close\r\n\r\n";

        public AgentHttpTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("http");
            AgentCredentials.SaveSecret("test-secret-0123456789abcdefghijklmn", "HW-TEST");
        }

        public void Dispose() { }

        [Fact]
        public void ClientDoesNotFollowRedirects() => Assert.False(AgentHttp.Handler.AllowAutoRedirect);

        [Fact]
        public async Task Redirect_IsNotFollowed_SecretStaysWithTheServer()
        {
            using var elsewhere = new FakeHttpServer(Status(200, "OK"));
            using var server = new FakeHttpServer(Status(302, "Found", $"Location: {elsewhere.Url}/steal\r\n"));

            PostResult result = await AgentHttp.PostAsync(server.Url, "/api/software/HW-TEST", "HW-TEST", new { items = new object[0] }, "test");

            Assert.Equal(PostResult.Failed, result);
            Assert.Equal(1, server.Requests);
            Assert.Contains("X-Agent-Secret", server.LastRequest);
            await Task.Delay(200);
            Assert.Equal(0, elsewhere.Requests);
        }

        [Theory]
        [InlineData(200, "OK", PostResult.Sent)]
        [InlineData(404, "Not Found", PostResult.EndpointMissing)]
        [InlineData(405, "Method Not Allowed", PostResult.EndpointMissing)]
        [InlineData(401, "Unauthorized", PostResult.Failed)]
        [InlineData(500, "Internal Server Error", PostResult.Failed)]
        public async Task StatusCodes_AreClassified(int code, string reason, PostResult expected)
        {
            using var server = new FakeHttpServer(Status(code, reason));
            Assert.Equal(expected, await AgentHttp.PostAsync(server.Url, "/api/patches/HW-TEST", "HW-TEST", new { pending_count = 0 }, "test"));
        }

        [Fact]
        public async Task PlainHttpToAnotherHost_IsNeverSent() =>
            Assert.Equal(PostResult.NotSent, await AgentHttp.PostAsync("http://pops.example", "/api/software/HW-TEST", "HW-TEST", new { }, "test"));

        // Yazılım envanteri: uç yoksa bir gün beklenir, 15 dk'da bir boşuna denenmez
        [Fact]
        public void SoftwareReporter_BacksOffOnMissingEndpoint()
        {
            Assert.Equal(SoftwareReporter.MaxSilence, SoftwareReporter.DelayAfter(PostResult.EndpointMissing));
            Assert.Equal(SoftwareReporter.RetryDelay, SoftwareReporter.DelayAfter(PostResult.Failed));
        }
    }

    // L11: kayıt defteri alanları ajanda da sunucunun sütun sınırlarına kısaltılır
    public class SoftwareClipTests : TestBase
    {
        [Fact]
        public void LongRegistryValues_AreClipped()
        {
            var entry = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["DisplayName"] = new string('n', 500),
                ["DisplayVersion"] = new string('v', 150),
                ["Publisher"] = new string('p', 250),
                ["InstallDate"] = new string('9', 30),
            };
            SoftwareItem item = SoftwareInventory.FromEntry(entry);
            Assert.Equal(SoftwareInventory.MaxName, item.Name.Length);
            Assert.Equal(SoftwareInventory.MaxVersion, item.Version.Length);
            Assert.Equal(SoftwareInventory.MaxPublisher, item.Publisher.Length);
            Assert.Equal(SoftwareInventory.MaxInstallDate, item.InstallDate.Length);
        }
    }

    // M2: gönderilemeyen durum saklanır, yalnızca gönderim yeniden denenir; uç yoksa bırakılır
    public class PatchDeliveryTests : TestBase, IDisposable
    {
        public PatchDeliveryTests() => AgentUpdate.DataDir = TestEnvironment.NewDir("patch");

        public void Dispose() => AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;

        private static PatchStatusPayload Status(string lastResult = null) =>
            PatchClassifier.BuildStatus(new List<PendingUpdate> { new PendingUpdate { Kb = "KB1", Title = "t", Severity = "Critical" } }, false, DateTime.UtcNow, null, lastResult);

        private static PatchManager Manager(PostResult result) =>
            new PatchManager("https://pops.example", () => "HW-A") { Poster = _ => Task.FromResult(result) };

        [Fact]
        public async Task FailedPost_KeepsTheReportForARetry()
        {
            DateTime lastScan = DateTime.UtcNow.AddMinutes(-2);
            PatchManager.SaveState(new PatchState { LastScanUtc = lastScan });

            await Manager(PostResult.Failed).DeliverAsync(Status("2 güncelleme kuruldu"));

            PatchState state = PatchManager.LoadState();
            Assert.Equal("2 güncelleme kuruldu", state.PendingReport.LastResult);
            Assert.InRange(state.NextPostUtc.Value, DateTime.UtcNow.AddMinutes(29), DateTime.UtcNow.AddMinutes(31));
            // Tarama zamanı değişmez: sonraki tam tarama yine 24 saat sonra
            Assert.Equal(lastScan, state.LastScanUtc);
        }

        [Fact]
        public async Task SuccessfulRetry_ClearsTheReport()
        {
            await Manager(PostResult.Failed).DeliverAsync(Status());
            await Manager(PostResult.Sent).DeliverAsync(PatchManager.LoadState().PendingReport);
            PatchState state = PatchManager.LoadState();
            Assert.Null(state.PendingReport);
            Assert.Null(state.NextPostUtc);
        }

        [Fact]
        public async Task MissingEndpoint_DropsTheReport()
        {
            await Manager(PostResult.EndpointMissing).DeliverAsync(Status());
            Assert.Null(PatchManager.LoadState().PendingReport);
        }

        [Fact]
        public void InvalidScope_StartsNothing()
        {
            PatchManager manager = Manager(PostResult.Sent);
            manager.RequestInstall("security\r\n[AGENT] sahte");
            Assert.False(manager.IsBusy);
        }
    }

    // L12: tepsiye giden mesajlar birden çok thread'den yazılsa da çerçeveler karışmaz
    public class PipeFrameTests : TestBase
    {
        // Her baytı ayrı yazan ve araya diğer thread'leri sokan akış: kilit yoksa çerçeveler iç içe geçer
        private sealed class TricklingStream : Stream
        {
            private readonly List<byte> _data = new List<byte>();
            public byte[] Data { get { lock (_data) return _data.ToArray(); } }
            public override void Write(byte[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    lock (_data) _data.Add(buffer[offset + i]);
                    if (i % 3 == 0) Thread.Yield();
                }
            }
            public override void Flush() { }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        [Fact]
        public void ConcurrentWrites_KeepFramesIntact()
        {
            var stream = new TricklingStream();
            var gate = new object();
            const int threads = 8, perThread = 60;
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                for (int i = 0; i < perThread; i++)
                    TrayPipeServer.WriteFrame(stream, gate, Encoding.UTF8.GetBytes($"{{\"action\":\"t{t}\",\"n\":{i},\"pad\":\"{new string('x', 20 + t)}\"}}"));
            });

            byte[] data = stream.Data;
            var seen = new HashSet<string>();
            int pos = 0;
            while (pos < data.Length)
            {
                int length = BitConverter.ToInt32(data, pos);
                Assert.InRange(length, 1, 200);
                string message = Encoding.UTF8.GetString(data, pos + 4, length);
                Assert.Matches("^\\{\"action\":\"t\\d\",\"n\":\\d+,\"pad\":\"x+\"\\}$", message);
                seen.Add(message);
                pos += 4 + length;
            }
            Assert.Equal(threads * perThread, seen.Count);
        }
    }
}
