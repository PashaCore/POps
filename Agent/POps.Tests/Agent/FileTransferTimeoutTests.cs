using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Dosya aktarımının süreleri (bkz. TransferWatchdog): istemcinin 100 sn'lik sınırı yok; veri aktığı sürece aktarım sürer,
    // veri akmazsa (servis: 2 dk) ya da toplam süre dolarsa (servis: 2 saat) "failed" olur. Süreler kısaltılır. Sunucu sahte
    // işleyicidir, yavaş ya da takılan akış sahte bir akıştır: soket yok, paralel çalışır. Takılan ve süresi dolan aktarımların
    // sonucu yükten bağımsızdır (öteki süre bir dakikadır); yavaş akışta parçalar arası 75 ms, boşta kalma sınırı 1,5 sn.
    public class FileTransferTimeoutTests : TestBase, IDisposable
    {
        private const string Server = "https://pops.example";
        private const string Secret = "files-secret-0123456789abcdefghijklm";
        private const string PushId = "Wt3q9xLZr0bYc2VnQ1sT4A", PullId = "Kq2PzT8vN5mR1xC7bL4wYg";
        private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(75);
        private static readonly TimeSpan Long = TimeSpan.FromMinutes(1);

        private readonly AgentHarness _agent;
        private readonly string _profiles = TestEnvironment.NewDir("users-timeout");
        // Sunucunun yanıtı: testin verdiği
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _server;
        private HttpRequestMessage _seen;

        public FileTransferTimeoutTests()
        {
            _agent = AgentHarness.Create("files-timeout", new Handler(this), new MachineSeams
            {
                Kiosk = new FakeKioskRegistry(),
                Profiles = () => (_profiles, Path.Combine(_profiles, "ali")),
            });
            _agent.Capabilities.Load();
            _agent.Credentials.SaveSecret(Secret, "HW-FILES");
            Directory.CreateDirectory(Path.Combine(_profiles, "ali"));
        }

        public void Dispose() => _agent.Dispose();

        private sealed class Handler : HttpMessageHandler
        {
            private readonly FileTransferTimeoutTests _t;
            public Handler(FileTransferTimeoutTests t) { _t = t; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _t._seen = request;
                return _t._server(request, cancellationToken);
            }
        }

        private sealed class Unused : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("dosya aktarımı bildirimlerin istemcisini kullandı");
        }

        // Okundukça parça parça veren akış: her parçadan önce gap beklenir; stallAfter parçadan sonra hiç veri gelmez (iptale
        // kadar)
        private sealed class SlowStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _chunk;
            private readonly TimeSpan _gap;
            private readonly int _stallAfter;
            private int _position, _chunks;

            public SlowStream(byte[] data, int chunk, TimeSpan gap, int stallAfter = int.MaxValue)
            {
                _data = data;
                _chunk = chunk;
                _gap = gap;
                _stallAfter = stallAfter;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_chunks >= _stallAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (_position >= _data.Length) return 0;
                await Task.Delay(_gap, cancellationToken);
                int count = Math.Min(Math.Min(_chunk, buffer.Length), _data.Length - _position);
                _data.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                _chunks++;
                return count;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("yalnızca zaman uyumsuz");
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // Sunucu tarafı: yüklenen gövdeyi yavaş alan hedef (her yazmada gap; stallAfter yazmadan sonra hiç almaz)
        private sealed class SlowSink : Stream
        {
            private readonly TimeSpan _gap;
            private readonly int _stallAfter;
            private int _writes;

            public SlowSink(TimeSpan gap, int stallAfter = int.MaxValue)
            {
                _gap = gap;
                _stallAfter = stallAfter;
            }

            public long Received { get; private set; }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_writes >= _stallAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
                await Task.Delay(_gap, cancellationToken);
                _writes++;
                Received += buffer.Length;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("yalnızca zaman uyumsuz");
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => Received;
            public override long Position { get => Received; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private FileTransfer Transfer(TimeSpan idle, TimeSpan total) =>
            new FileTransfer(_agent.Paths, _agent.Http, _agent.Credentials, _agent.Machine.PublicDesktop, _agent.Machine.Profiles)
            {
                IdleTimeout = idle,
                TotalTimeout = total,
            };

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static FileTransfer.PushRequest Push(byte[] content, long? size = null)
        {
            JsonElement command = JsonSerializer.SerializeToElement(new
            {
                action = "file_push", transfer_id = PushId, name = "video.mp4", size = size ?? content.Length, sha256 = Sha(content),
                url = "/api/files/" + PushId + "/download?t=abc", dest = "inbox", reason = "ders videosu",
            });
            Assert.True(FileTransfer.TryParsePush(command, Server, out FileTransfer.PushRequest request, out string error), error);
            return request;
        }

        private FileTransfer.PullRequest Pull(byte[] content)
        {
            string file = Path.Combine(_profiles, "ali", "kayit.bin");
            File.WriteAllBytes(file, content);
            JsonElement command = JsonSerializer.SerializeToElement(new
            {
                action = "file_pull", transfer_id = PullId, path = file, max_size = FileTransfer.MaxBytes,
                upload = "/api/files/" + PullId + "/upload?t=x", reason = "inceleme",
            });
            Assert.True(FileTransfer.TryParsePull(command, Server, out FileTransfer.PullRequest request, out string error), error);
            return request;
        }

        private void Download(SlowStream body) =>
            _server = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

        private void Upload(SlowSink sink) =>
            _server = async (request, token) =>
            {
                await request.Content.CopyToAsync(sink, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

        private string InboxDir => Path.Combine(_agent.Paths.DataDir, "inbox", "2026-10-05");

        // ------------------------------------------------------------------ istemci
        [Fact]
        public void Transfers_HaveTheirOwnClient_WithoutTheReportsTimeout()
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, _agent.Http.TransferClient.Timeout);
            Assert.Equal(TimeSpan.FromSeconds(100), _agent.Http.Client.Timeout);
            Assert.NotSame(_agent.Http.Client, _agent.Http.TransferClient);
            FileTransfer transfer = new FileTransfer(_agent.Paths, _agent.Http, _agent.Credentials, _agent.Machine.PublicDesktop, _agent.Machine.Profiles);
            Assert.Equal((TimeSpan.FromMinutes(2), TimeSpan.FromHours(2)), (transfer.IdleTimeout, transfer.TotalTimeout));
        }

        // Aktarım istemcisi ajanın işleyicisini kullanır (serviste ServerTrust'ın işleyicisi: sertifika denetimi, yönlendirme
        // yok): istek bu bağlamın işleyicisine cihaz anahtarıyla gelir
        [Fact]
        public async Task TransferClient_UsesTheAgentsHandler_AndTheDeviceKey()
        {
            byte[] content = RandomNumberGenerator.GetBytes(1000);
            Download(new SlowStream(content, 1000, TimeSpan.Zero));
            // Bildirimlerin istemcisi kullanılmaz
            _agent.Http.Client = new HttpClient(new Unused());
            var (outcome, _, detail) = await Transfer(Long, Long).PushAsync(Push(content), "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.True(outcome == "done", detail);
            Assert.Equal("HW-FILES", Assert.Single(_seen.Headers.GetValues("X-Agent-Id")));
            Assert.Equal(Secret, Assert.Single(_seen.Headers.GetValues("X-Agent-Secret")));

            // Serviste (Program) iki istemcinin ortak işleyicisi: yönlendirme izlenmez, sunucu sertifikası ServerTrust'la denetlenir
            var trusted = new AgentHttp(_agent.Trust.NewHandler(), _agent.Credentials);
            Assert.False(trusted.Handler.AllowAutoRedirect);
            Assert.NotNull(trusted.Handler.ServerCertificateCustomValidationCallback);
        }

        [Fact]
        public void TimeoutDetails_SayWhatHappened()
        {
            Assert.Equal("zaman aşımı: 2 dakikadır veri yok", TransferWatchdog.IdleMessage(FileTransfer.DefaultIdleTimeout));
            Assert.Equal("toplam süre 2 saati aştı", TransferWatchdog.TotalMessage(FileTransfer.DefaultTotalTimeout));
            Assert.Equal("zaman aşımı: 0.3 saniyedir veri yok", TransferWatchdog.IdleMessage(TimeSpan.FromMilliseconds(300)));
            Assert.Equal("toplam süre 90 dakikayı aştı", TransferWatchdog.TotalMessage(TimeSpan.FromMinutes(90)));
            Assert.Equal("toplam süre 0.4 saniyeyi aştı", TransferWatchdog.TotalMessage(TimeSpan.FromMilliseconds(400)));

            // Sunucunun file_result şeması: önceki "failed" biçimi, açıklama süreyi söyler; başka alan yok
            JsonElement result = JsonSerializer.SerializeToElement(FileTransfer.Result(PullId, "failed", "C:\\Users\\ali\\kayit.bin", TransferWatchdog.IdleMessage(FileTransfer.DefaultIdleTimeout)));
            Assert.Equal(new[] { "type", "transfer_id", "outcome", "path", "detail" }, result.EnumerateObject().Select(p => p.Name));
            Assert.Equal(("file_result", PullId, "failed", "C:\\Users\\ali\\kayit.bin", "zaman aşımı: 2 dakikadır veri yok"),
                (result.GetProperty("type").GetString(), result.GetProperty("transfer_id").GetString(), result.GetProperty("outcome").GetString(),
                    result.GetProperty("path").GetString(), result.GetProperty("detail").GetString()));
        }

        // ------------------------------------------------------------------ gönderme (sunucudan indirme)
        // Toplam süre (30 parça, en az 2,25 sn) boşta kalma sınırından uzun; her parça sınırın çok altında gelir: aktarım biter
        [Fact]
        public async Task Push_SlowButProgressing_IsNotCut()
        {
            byte[] content = RandomNumberGenerator.GetBytes(30 * 1000);
            Download(new SlowStream(content, 1000, Gap));
            var (outcome, path, detail) = await Transfer(TimeSpan.FromMilliseconds(1500), Long)
                .PushAsync(Push(content), "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.True(outcome == "done", detail);
            Assert.Equal(content, File.ReadAllBytes(path));
        }

        [Fact]
        public async Task Push_Stalled_FailsWithTheIdleTimeout()
        {
            byte[] content = RandomNumberGenerator.GetBytes(10 * 1000);
            Download(new SlowStream(content, 1000, TimeSpan.Zero, stallAfter: 3));
            var (outcome, path, detail) = await Transfer(TimeSpan.FromMilliseconds(300), Long)
                .PushAsync(Push(content), "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.Equal(("failed", null, "zaman aşımı: 0.3 saniyedir veri yok"), (outcome, path, detail));
            Assert.Empty(Directory.GetFiles(InboxDir));
            Assert.Empty(Directory.GetFiles(_agent.Paths.SecureDir, "transfer-*"));   // yarım dosya kalmaz
        }

        // Yanıt başlığı hiç gelmezse de (sunucu bekletiyor) aynı süre işler
        [Fact]
        public async Task Push_NoResponse_FailsWithTheIdleTimeout()
        {
            _server = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };
            var (outcome, _, detail) = await Transfer(TimeSpan.FromMilliseconds(300), Long)
                .PushAsync(Push(new byte[10]), "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.Equal(("failed", "zaman aşımı: 0.3 saniyedir veri yok"), (outcome, detail));
        }

        // Veri aksa da toplam süre dolunca kesilir
        [Fact]
        public async Task Push_Endless_FailsWithTheTotalTimeout()
        {
            byte[] content = RandomNumberGenerator.GetBytes(200 * 1000);
            Download(new SlowStream(content, 100, TimeSpan.FromMilliseconds(10)));
            var (outcome, _, detail) = await Transfer(Long, TimeSpan.FromMilliseconds(400))
                .PushAsync(Push(content), "HW-FILES", new DateTime(2026, 10, 5), CancellationToken.None);
            Assert.Equal(("failed", "toplam süre 0.4 saniyeyi aştı"), (outcome, detail));
            Assert.Empty(Directory.GetFiles(InboxDir));
        }

        // Servis duruyor: süre açıklaması yok (aktarım süre yüzünden kesilmedi)
        [Fact]
        public async Task Push_ServiceStopping_IsNotATimeout()
        {
            byte[] content = RandomNumberGenerator.GetBytes(10 * 1000);
            Download(new SlowStream(content, 1000, TimeSpan.Zero, stallAfter: 1));
            using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var (outcome, _, detail) = await Transfer(Long, Long).PushAsync(Push(content), "HW-FILES", new DateTime(2026, 10, 5), stopping.Token);
            Assert.Equal("failed", outcome);
            Assert.DoesNotContain("zaman aşımı", detail);
            Assert.DoesNotContain("toplam süre", detail);
        }

        // ------------------------------------------------------------------ alma (sunucuya yükleme)
        // Sunucu her parçayı yavaş alır; toplam süre boşta kalma sınırını aşar: yükleme biter. StreamContent 128 KB'lık parçalar
        // yazar (havuzdan aldığı tampon): 32 parça, en az 2,4 sn
        [Fact]
        public async Task Pull_SlowButProgressing_IsNotCut()
        {
            byte[] content = RandomNumberGenerator.GetBytes(32 * 128 * 1024);
            var sink = new SlowSink(Gap);
            Upload(sink);
            var (outcome, _, detail, size) = await Transfer(TimeSpan.FromMilliseconds(1500), Long).PullAsync(Pull(content), "HW-FILES", CancellationToken.None);
            Assert.True(outcome == "done", detail);
            Assert.Equal(content.Length, size);
            Assert.Equal(content.Length, sink.Received);
            Assert.Equal(Secret, Assert.Single(_seen.Headers.GetValues("X-Agent-Secret")));
        }

        [Fact]
        public async Task Pull_Stalled_FailsWithTheIdleTimeout()
        {
            byte[] content = RandomNumberGenerator.GetBytes(10 * 81920);
            Upload(new SlowSink(TimeSpan.Zero, stallAfter: 2));
            var (outcome, path, detail, _) = await Transfer(TimeSpan.FromMilliseconds(300), Long).PullAsync(Pull(content), "HW-FILES", CancellationToken.None);
            Assert.Equal(("failed", "zaman aşımı: 0.3 saniyedir veri yok"), (outcome, detail));
            Assert.Equal(Path.GetFullPath(Path.Combine(_profiles, "ali", "kayit.bin")), Path.GetFullPath(path));
        }

        [Fact]
        public async Task Pull_TooLongInAll_FailsWithTheTotalTimeout()
        {
            byte[] content = RandomNumberGenerator.GetBytes(40 * 81920);
            Upload(new SlowSink(TimeSpan.FromMilliseconds(25)));
            var (outcome, _, detail, _) = await Transfer(Long, TimeSpan.FromMilliseconds(400)).PullAsync(Pull(content), "HW-FILES", CancellationToken.None);
            Assert.Equal(("failed", "toplam süre 0.4 saniyeyi aştı"), (outcome, detail));
        }
    }
}
