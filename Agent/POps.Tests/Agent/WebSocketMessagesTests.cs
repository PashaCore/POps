using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class WebSocketMessagesTests : TestBase
    {
        private const int CommandLimit = 8 * 1024 * 1024; // Worker.MaxCommandMessageBytes

        // Loopback üzerinde birbirine bağlı sunucu/istemci WebSocket çifti (HTTP el sıkışması olmadan)
        private static async Task<(WebSocket Server, WebSocket Client, IDisposable Cleanup)> PairAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            TcpClient accepted = await listener.AcceptTcpClientAsync();
            await connect;
            listener.Stop();
            var server = WebSocket.CreateFromStream(accepted.GetStream(), true, null, TimeSpan.FromMinutes(1));
            var ws = WebSocket.CreateFromStream(client.GetStream(), false, null, TimeSpan.FromMinutes(1));
            return (server, ws, new Cleanup(accepted, client));
        }

        private sealed class Cleanup : IDisposable
        {
            private readonly TcpClient _a, _b;
            public Cleanup(TcpClient a, TcpClient b) { _a = a; _b = b; }
            public void Dispose() { _a.Dispose(); _b.Dispose(); }
        }

        private static Task Send(WebSocket ws, string text, bool end) =>
            ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, end, CancellationToken.None);

        [Fact]
        public async Task FragmentedMessage_IsReassembled()
        {
            var (server, client, cleanup) = await PairAsync();
            using (cleanup)
            {
                await Send(server, "{\"action\":\"exec", false);
                await Send(server, "ute\",\"script_path\":\"dir", false);
                await Send(server, " C:\\\\\"}", true);
                var (text, type) = await WebSocketMessages.ReceiveTextAsync(client, new byte[16384], CommandLimit, CancellationToken.None);
                Assert.Equal(WebSocketMessageType.Text, type);
                Assert.Equal("{\"action\":\"execute\",\"script_path\":\"dir C:\\\\\"}", text);
            }
        }

        [Fact]
        public async Task MessageLargerThanTheReadBuffer_IsReadWhole()
        {
            var (server, client, cleanup) = await PairAsync();
            using (cleanup)
            {
                string big = "{\"script\":\"" + new string('x', 200_000) + "\"}";
                Task sending = Send(server, big, true);
                var (text, _) = await WebSocketMessages.ReceiveTextAsync(client, new byte[16384], CommandLimit, CancellationToken.None);
                await sending;
                Assert.Equal(big, text);
            }
        }

        [Fact]
        public async Task MessageOverTheLimit_IsDrainedAndRejected_NextMessageIntact()
        {
            var (server, client, cleanup) = await PairAsync();
            using (cleanup)
            {
                string chunk = new string('y', 1024 * 1024);
                Task sending = Task.Run(async () =>
                {
                    for (int i = 0; i < 8; i++) await Send(server, chunk, false);
                    await Send(server, chunk, true); // 9 MB toplam
                    await Send(server, "{\"after\":1}", true);
                });

                var buffer = new byte[16384];
                await Assert.ThrowsAsync<WebSocketMessages.TooLargeException>(() => WebSocketMessages.ReceiveTextAsync(client, buffer, CommandLimit, CancellationToken.None));
                var (next, _) = await WebSocketMessages.ReceiveTextAsync(client, buffer, CommandLimit, CancellationToken.None);
                await sending;
                Assert.Equal("{\"after\":1}", next);
            }
        }

        [Fact]
        public async Task CloseFrame_IsReported()
        {
            var (server, client, cleanup) = await PairAsync();
            using (cleanup)
            {
                Task closing = server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                var (text, type) = await WebSocketMessages.ReceiveTextAsync(client, new byte[1024], CommandLimit, CancellationToken.None);
                await closing;
                Assert.Null(text);
                Assert.Equal(WebSocketMessageType.Close, type);
            }
        }
    }
}
