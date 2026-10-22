using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace POps.Tests.Agent
{
    // Ajanın komut bağlantısı (/ws/agent) için 127.0.0.1'de sahte sunucu. HTTP el sıkışması elle yapılır (http.sys ve
    // yönetici izni gerekmez); her bağlantının yolu, istek başlıkları ve ajanın gönderdiği metin mesajları sırayla tutulur.
    internal sealed class FakeCommandServer : IDisposable
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Channel<Connection> _connections = Channel.CreateUnbounded<Connection>();
        private readonly List<IDisposable> _open = new List<IDisposable>();

        public FakeCommandServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        // Ajanın açtığı bir sonraki bağlantı (yoksa süre dolunca TimeoutException)
        public async Task<Connection> NextConnectionAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try { return await _connections.Reader.ReadAsync(cts.Token); }
            catch (OperationCanceledException) { throw new TimeoutException($"ajan {timeout.TotalSeconds:0} sn içinde bağlanmadı"); }
        }

        // Süre içinde yeni bağlantı geldi mi (gelirse kuyrukta kalır)
        public async Task<bool> ConnectsWithinAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try { return await _connections.Reader.WaitToReadAsync(cts.Token); }
            catch (OperationCanceledException) { return false; }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                lock (_open) _open.Add(client);
                _ = Task.Run(() => UpgradeAsync(client));
            }
        }

        private async Task UpgradeAsync(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                var request = new List<byte>();
                byte[] one = new byte[1];
                while (!EndsWithBlankLine(request))
                {
                    if (await stream.ReadAsync(one, 0, 1) == 0) return;
                    request.Add(one[0]);
                }
                string[] lines = Encoding.ASCII.GetString(request.ToArray()).Split("\r\n");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines[1..])
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + WebSocketGuid)));
                byte[] response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length);
                WebSocket socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromMinutes(1));
                var connection = new Connection(lines[0].Split(' ')[1], headers, socket);
                lock (_open) _open.Add(socket);
                await _connections.Writer.WriteAsync(connection);
                await connection.ReceiveLoopAsync();
            }
            catch (Exception) { }
        }

        private static bool EndsWithBlankLine(List<byte> data)
        {
            int n = data.Count;
            return n >= 4 && data[n - 4] == '\r' && data[n - 3] == '\n' && data[n - 2] == '\r' && data[n - 1] == '\n';
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            lock (_open) foreach (IDisposable item in _open) item.Dispose();
            _cts.Dispose();
        }

        // Ajanın bir komut bağlantısı
        public sealed class Connection
        {
            private readonly WebSocket _socket;
            private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
            private readonly List<string> _received = new List<string>();

            public Connection(string path, Dictionary<string, string> headers, WebSocket socket)
            {
                Path = path;
                Headers = headers;
                _socket = socket;
            }

            public string Path { get; }
            public Dictionary<string, string> Headers { get; }

            // Ajanın bu bağlantıda şimdiye kadar gönderdiği mesajlar
            public List<string> Received { get { lock (_received) return new List<string>(_received); } }

            internal async Task ReceiveLoopAsync()
            {
                byte[] buffer = new byte[64 * 1024];
                var message = new List<byte>();
                while (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseSent)
                {
                    WebSocketReceiveResult result;
                    try { result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); }
                    catch (Exception) { break; }
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;
                    string text = Encoding.UTF8.GetString(message.ToArray());
                    message.Clear();
                    lock (_received) _received.Add(text);
                    await _messages.Writer.WriteAsync(text);
                }
                _messages.Writer.TryComplete();
            }

            // Ajanın bu bağlantıda gönderdiği bir sonraki mesaj (yoksa süre dolunca TimeoutException)
            public async Task<JsonElement> NextAsync(TimeSpan timeout)
            {
                using var cts = new CancellationTokenSource(timeout);
                try
                {
                    string text = await _messages.Reader.ReadAsync(cts.Token);
                    using JsonDocument doc = JsonDocument.Parse(text);
                    return doc.RootElement.Clone();
                }
                catch (OperationCanceledException) { throw new TimeoutException($"ajan {timeout.TotalSeconds:0} sn içinde mesaj göndermedi"); }
            }

            // Süre içinde mesaj geldi mi (gelirse kuyrukta kalır)
            public async Task<bool> SendsWithinAsync(TimeSpan timeout)
            {
                using var cts = new CancellationTokenSource(timeout);
                try { return await _messages.Reader.WaitToReadAsync(cts.Token); }
                catch (OperationCanceledException) { return false; }
            }

            public Task SendAsync(string json) =>
                _socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);

            // Sunucu bağlantıyı bu kodla kapatır (4401 kimlik reddi, 4409 kopya, 1000 olağan)
            public Task CloseAsync(int code) =>
                _socket.CloseOutputAsync((WebSocketCloseStatus)code, "test", CancellationToken.None);
        }
    }
}
