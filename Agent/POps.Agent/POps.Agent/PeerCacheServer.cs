using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace POpsAgent
{
    // Eş önbelleğinin salt okunur HTTP sunucusu (bkz. PeerCache). HttpListener yerine TcpListener: http.sys trafiği
    // System sürecine ait sayılır, güvenlik duvarı kuralı POpsAgent.exe'ye bağlanamazdı; burada soket ajanın kendisinde.
    //  * Yalnızca "GET /pops-cache/<sha256> HTTP/1.x" (ve gövdesiz HEAD), <sha256> önbellekteki paket: 200,
    //    application/octet-stream, Content-Length. Başka yöntem 405, başka yol (liste, başka dosya, sorgu, büyük harf)
    //    404, bozuk istek 400.
    //  * Bağlanan adres bu bilgisayarın yerel alt ağlarından birinde değilse 403 (güvenlik duvarına ek savunma).
    //  * En çok MaxClients eşzamanlı aktarım; fazlası boş yer için en çok 60 sn bekler, sonra 503 alır (40 PC'lik bir
    //    sınıf, tohum birkaç saniye meşgul diye sunucuya dönmesin). Bekleyenlerle birlikte en çok MaxConnections bağlantı
    //    (fazlası hemen 503). İstek başlığı için 10 sn, aktarım için 10 dk süre; kalıcı bağlantı, Range ve sıkıştırma yok.
    [SupportedOSPlatform("windows")]
    internal sealed class PeerCacheServer : IDisposable
    {
        private const int MaxHeaderBytes = 8192;
        // Aktarım bekleyenler dahil açık bağlantı sınırı (bir sınıfın hepsi aynı anda sorabilir)
        internal const int MaxConnections = 128;
        private static readonly byte[] BusyResponse = Encoding.ASCII.GetBytes(StatusResponse(503));

        private readonly string _path;
        private readonly TcpListener _listener;
        private readonly Func<IPAddress, bool> _isLocal;
        private readonly SemaphoreSlim _slots;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly List<Task> _handlers = new List<Task>();
        private readonly TimeSpan _headerTimeout;
        private readonly TimeSpan _transferTimeout;
        private readonly TimeSpan _slotWait;
        private Task? _acceptLoop;
        private int _active;
        private int _connections;
        private DateTime _lastRefusalLog = DateTime.MinValue;
        private int _suppressedRefusals;

        public PeerCacheServer(string sha256, string path, IPEndPoint endpoint, Func<IPAddress, bool> isLocal, int maxClients,
            TimeSpan? headerTimeout = null, TimeSpan? transferTimeout = null, TimeSpan? slotWait = null)
        {
            Sha256 = sha256;
            _path = path;
            _isLocal = isLocal;
            _slots = new SemaphoreSlim(maxClients, maxClients);
            _headerTimeout = headerTimeout ?? TimeSpan.FromSeconds(10);
            _transferTimeout = transferTimeout ?? TimeSpan.FromMinutes(10);
            _slotWait = slotWait ?? TimeSpan.FromSeconds(60);
            _listener = new TcpListener(endpoint);
            // Başka bir süreç aynı portu paylaşıp istekleri kapamasın
            _listener.ExclusiveAddressUse = true;
            // [::]:8817 hem IPv6 hem IPv4 bağlantılarını alır
            if (endpoint.Address.Equals(IPAddress.IPv6Any)) _listener.Server.DualMode = true;
        }

        public string Sha256 { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        // Aktarımdaki istemciler (yer tutanlar) ve açık bağlantılar (yer bekleyenler dahil)
        public int ActiveClients => Volatile.Read(ref _active);
        public int Connections => Volatile.Read(ref _connections);

        // Testler: yer alındıktan sonra, yanıt yazılmadan önce çağrılır (aktarımı tutmak için)
        internal Func<Task>? TransferStarting { get; set; }

        public void Start()
        {
            _listener.Start(16);
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        // Dinleme durur, süren aktarımlar kesilir (indiren sıradaki kaynağa geçer)
        public async Task StopAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            if (_acceptLoop != null) await _acceptLoop;
            Task[] running;
            lock (_handlers) running = _handlers.ToArray();
            try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            _stop.Dispose();
            _slots.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await _listener.AcceptSocketAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                catch (SocketException) when (_stop.IsCancellationRequested) { return; }
                catch (SocketException ex)
                {
                    // Bağlantı kabulden önce koptu vb.: dinleme sürer
                    POpsHelpers.Log("PEERCACHE", $"Bağlantı kabul edilemedi: {ex.Message}", true);
                    continue;
                }

                if (Interlocked.Increment(ref _connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref _connections);
                    Refuse(socket);
                    continue;
                }
                Task handler = Task.Run(() => HandleAsync(socket));
                lock (_handlers)
                {
                    _handlers.RemoveAll(t => t.IsCompleted);
                    _handlers.Add(handler);
                }
            }
        }

        // Bağlantı sınırı dolu: kısa 503 (yeni soketin gönderme tamponu boştur, beklemez) ve kapatma
        private static void Refuse(Socket socket)
        {
            try
            {
                socket.Send(BusyResponse, SocketFlags.None);
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally { socket.Dispose(); }
        }

        private async Task HandleAsync(Socket socket)
        {
            IPAddress? remote = null;
            bool slot = false;
            var watch = Stopwatch.StartNew();
            using var stream = new NetworkStream(socket, ownsSocket: true);
            try
            {
                remote = (socket.RemoteEndPoint as IPEndPoint)?.Address;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                cts.CancelAfter(_headerTimeout);
                string? head = await ReadHeadAsync(stream, cts.Token);

                int status = Decide(head, remote);
                if (status != 200)
                {
                    await WriteAsync(stream, StatusResponse(status), cts.Token);
                    LogRefusal(status, remote, head);
                    return;
                }

                // Boş aktarım yeri beklenir (en çok _slotWait; başlık süresi bu sırada işlemez); gelmezse 503, indiren
                // sıradaki kaynağa geçer
                cts.CancelAfter(Timeout.InfiniteTimeSpan);
                slot = await _slots.WaitAsync(_slotWait, _stop.Token);
                cts.CancelAfter(_headerTimeout);
                if (!slot)
                {
                    await WriteAsync(stream, StatusResponse(503), cts.Token);
                    LogRefusal(503, remote, head);
                    return;
                }
                Interlocked.Increment(ref _active);
                if (TransferStarting != null) await TransferStarting();
                bool headOnly = head!.StartsWith("HEAD ", StringComparison.Ordinal);

                FileStream file;
                try { file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan); }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    await WriteAsync(stream, StatusResponse(404), cts.Token);
                    return;
                }
                await using (file)
                {
                    cts.CancelAfter(_transferTimeout);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n"
                        + $"Content-Length: {file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n"
                        + "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n", cts.Token);
                    if (!headOnly) await file.CopyToAsync(stream, 81920, cts.Token);
                    await stream.FlushAsync(cts.Token);
                    socket.Shutdown(SocketShutdown.Send);
                    if (!headOnly)
                        POpsHelpers.Log("PEERCACHE", $"Paket eşe gönderildi: {Describe(remote)} ({Sha256.Substring(0, 12)}…, {file.Length} bayt, {watch.Elapsed.TotalSeconds:0.0} sn).");
                }
            }
            catch (OperationCanceledException)
            {
                if (!_stop.IsCancellationRequested) POpsHelpers.Log("PEERCACHE", $"Eş aktarımı süre aşımıyla kesildi: {Describe(remote)}.", true);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is UnauthorizedAccessException)
            {
                // İstemci bağlantıyı kapattı ya da sunucu duruyor: indiren sıradaki kaynağa geçer
                if (!_stop.IsCancellationRequested) POpsHelpers.Log("PEERCACHE", $"Eş aktarımı yarıda kaldı: {Describe(remote)}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _connections);
                if (slot)
                {
                    Interlocked.Decrement(ref _active);
                    try { _slots.Release(); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        // 200 ya da hata kodu. Önce adres: yerel alt ağ dışı hiçbir şey öğrenemez.
        internal int Decide(string? head, IPAddress? remote)
        {
            if (remote == null || !_isLocal(PeerCache.Normalize(remote))) return 403;
            if (head == null) return 400;
            int end = head.IndexOf("\r\n", StringComparison.Ordinal);
            string[] parts = (end < 0 ? head : head.Substring(0, end)).Split(' ');
            if (parts.Length != 3 || (parts[2] != "HTTP/1.1" && parts[2] != "HTTP/1.0")) return 400;
            if (parts[0] != "GET" && parts[0] != "HEAD") return 405;
            if (!string.Equals(parts[1], PeerCache.PathPrefix + Sha256, StringComparison.Ordinal)) return 404;
            return 200;
        }

        // İstek satırı ve başlıklar ("\r\n\r\n"e kadar, en çok 8 KB); bağlantı kapandıysa ya da çok uzunsa null
        private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] buffer = new byte[MaxHeaderBytes];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(length), token);
                if (read == 0) return null;
                int searchFrom = Math.Max(0, length - 3);
                length += read;
                for (int i = searchFrom; i + 3 < length; i++)
                    if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                        return Encoding.Latin1.GetString(buffer, 0, i);
            }
            return null;
        }

        private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken token) =>
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text), token);

        private static string StatusResponse(int status)
        {
            string reason = status switch
            {
                400 => "Bad Request",
                403 => "Forbidden",
                404 => "Not Found",
                405 => "Method Not Allowed",
                503 => "Service Unavailable",
                _ => "Error",
            };
            string extra = status == 405 ? "Allow: GET, HEAD\r\n" : status == 503 ? "Retry-After: 30\r\n" : "";
            return $"HTTP/1.1 {status.ToString(System.Globalization.CultureInfo.InvariantCulture)} {reason}\r\nContent-Length: 0\r\n{extra}Connection: close\r\n\r\n";
        }

        // Reddedilen istekler loga en çok 10 sn'de bir yazılır (yerel ağdan sürekli istek logu doldurmasın)
        private void LogRefusal(int status, IPAddress? remote, string? head)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastRefusalLog < TimeSpan.FromSeconds(10))
            {
                Interlocked.Increment(ref _suppressedRefusals);
                return;
            }
            _lastRefusalLog = now;
            int suppressed = Interlocked.Exchange(ref _suppressedRefusals, 0);
            string line = head == null ? "(istek yok)" : LogText.Safe(head.Split('\r')[0], 120);
            POpsHelpers.Log("PEERCACHE", $"Eş önbelleği isteği reddedildi ({status}): {Describe(remote)} {line}" + (suppressed > 0 ? $" (+{suppressed} benzeri)" : "") + ".", status == 403);
        }

        private static string Describe(IPAddress? remote) => remote == null ? "(adres yok)" : PeerCache.Normalize(remote).ToString();
    }
}
