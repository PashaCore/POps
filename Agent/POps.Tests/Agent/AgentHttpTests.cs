using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // AgentHttp: yönlendirme izlenmez (L10), 404/405 "uç yok" sayılır (M2). Gerçek bir TCP dinleyicisiyle, yalnızca
    // 127.0.0.1 üzerinde denenir.
    [Collection(SharedStateCollection.Name)]
    public class AgentHttpTests : SharedStateTestBase, IDisposable
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
    }

    public class BoundedResponseTests : TestBase
    {
        // Parçalı (chunked) yanıtta Content-Length yoktur: akış sınırlı okunur
        private sealed class NoLengthStream : MemoryStream
        {
            public NoLengthStream(byte[] data) : base(data) { }
            public override bool CanSeek => false;
        }

        [Fact]
        public async Task ResponseWithoutLength_IsCapped()
        {
            byte[] big = Encoding.UTF8.GetBytes(new string('x', 5000));
            Assert.Null(await AgentHttp.ReadLimitedAsync(new StreamContent(new NoLengthStream(big)), 1024));
            Assert.Equal(5000, (await AgentHttp.ReadLimitedAsync(new StreamContent(new NoLengthStream(big)), 10000)).Length);
            Assert.Null(await AgentHttp.ReadLimitedAsync(new ByteArrayContent(big), 1024));
        }
    }
}
