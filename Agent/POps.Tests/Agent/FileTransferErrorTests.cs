using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Dosya aktarımının "failed" açıklaması (bkz. TransferError): .NET'in ham iletisi ("Error while copying content to a stream.")
    // değil, ne olduğu Türkçe, ham ileti parantez içinde. Saha (2026-10-22): Cloudflare'in arkasındaki sunucuya 190 MB'lık
    // file_pull, vekil 100 MB'tan büyük gövdeyi kesti; yönetici yalnızca ham iletiyi gördü. Sunucu sahte işleyicidir, kesilen
    // bağlantı sahte akışların fırlattığı (SocketsHttpHandler'ınki gibi) istisnadır: soket yok, paralel çalışır. Eşleme türe,
    // HttpRequestError'a ve SocketError'a göredir; iletiler kasten başka dilde de verilir.
    public class FileTransferErrorTests : TestBase, IDisposable
    {
        private const string Server = "https://pops.example";
        private const string Secret = "files-secret-0123456789abcdefghijklm";
        private const string PushId = "Wt3q9xLZr0bYc2VnQ1sT4A", PullId = "Kq2PzT8vN5mR1xC7bL4wYg";
        private static readonly TimeSpan Long = TimeSpan.FromMinutes(1);

        private const string UploadCut = "bağlantı kesildi: sunucu ya da önündeki vekil sunucu yüklemeyi yarıda kesti; olası neden vekilin "
            + "boyut sınırı (ör. Cloudflare 100 MB)";
        private const string DownloadCut = "bağlantı kesildi: indirme yarıda kaldı; sunucu ya da aradaki bir vekil sunucu bağlantıyı kapattı";

        private readonly AgentHarness _agent;
        private readonly string _profiles = TestEnvironment.NewDir("users-errors");
        // Sunucunun yanıtı: testin verdiği
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _server;
        // Sahte akışın ya da sunucunun fırlattığı istisna (beklenen ham ileti ondan)
        private Exception _thrown;

        public FileTransferErrorTests()
        {
            _agent = AgentHarness.Create("files-errors", new Handler(this), new MachineSeams
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
            private readonly FileTransferErrorTests _t;
            public Handler(FileTransferErrorTests t) { _t = t; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _t._server(request, cancellationToken);
        }

        // Sunucu tarafı: yüklenen gövdeyi alan hedef; okAfter yazmadan sonra bağlantı kesilir (error fırlatılır). stall: kesmeden
        // önce iptale kadar bekler (süre dolunca ya da servis durunca soket kapanır)
        private sealed class CuttingSink : Stream
        {
            private readonly int _okAfter;
            private readonly Func<Exception> _error;
            private readonly bool _stall;
            private int _writes;

            public CuttingSink(int okAfter, Func<Exception> error, bool stall = false)
            {
                _okAfter = okAfter;
                _error = error;
                _stall = stall;
            }

            public Exception Thrown { get; private set; }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_writes++ < _okAfter) return;
                if (_stall)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { }
                }
                throw Thrown = _error();
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("yalnızca zaman uyumsuz");
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        // İndirilen yanıtın gövdesi: okAfter parçadan sonra bağlantı kesilir (error, ya da stall ile iptale kadar bekleyip error;
        // error null ise iptalin kendisi)
        private sealed class CuttingBody : Stream
        {
            private readonly int _okAfter;
            private readonly Func<Exception> _error;
            private readonly bool _stall;
            private int _reads;

            public CuttingBody(int okAfter, Func<Exception> error, bool stall = false)
            {
                _okAfter = okAfter;
                _error = error;
                _stall = stall;
            }

            public Exception Thrown { get; private set; }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_reads++ < _okAfter)
                {
                    int count = Math.Min(buffer.Length, 1000);
                    buffer.Span.Slice(0, count).Fill(7);
                    return count;
                }
                if (_stall)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException ex) when (_error == null) { Thrown = ex; throw; }
                    catch (OperationCanceledException) { }
                }
                throw Thrown = _error();
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("yalnızca zaman uyumsuz");
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private FileTransfer Transfer(TimeSpan? idle = null) =>
            new FileTransfer(_agent.Paths, _agent.Http, _agent.Credentials, _agent.Machine.PublicDesktop, _agent.Machine.Profiles)
            {
                IdleTimeout = idle ?? Long,
                TotalTimeout = Long,
            };

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static FileTransfer.PushRequest Push(int size = 10 * 1000)
        {
            JsonElement command = JsonSerializer.SerializeToElement(new
            {
                action = "file_push", transfer_id = PushId, name = "video.mp4", size, sha256 = Sha(new byte[size]),
                url = "/api/files/" + PushId + "/download?t=abc", dest = "inbox", reason = "ders videosu",
            });
            Assert.True(FileTransfer.TryParsePush(command, Server, out FileTransfer.PushRequest request, out string error), error);
            return request;
        }

        private string PullFile(int size)
        {
            string file = Path.Combine(_profiles, "ali", "kayit.bin");
            File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(size));
            return file;
        }

        private object PullCommand(string file) => new
        {
            action = "file_pull", transfer_id = PullId, path = file, max_size = FileTransfer.MaxBytes,
            upload = "/api/files/" + PullId + "/upload?t=x", reason = "inceleme",
        };

        private FileTransfer.PullRequest Pull(int size = 10 * 81920)
        {
            JsonElement command = JsonSerializer.SerializeToElement(PullCommand(PullFile(size)));
            Assert.True(FileTransfer.TryParsePull(command, Server, out FileTransfer.PullRequest request, out string error), error);
            return request;
        }

        // Yön: upload dosya alma (PC -> sunucu), değilse dosya gönderme (sunucudan indirme)
        private async Task<string> Run(bool upload, TimeSpan? idle = null, CancellationToken stopping = default)
        {
            if (upload)
            {
                var (outcome, path, detail, _) = await Transfer(idle).PullAsync(Pull(), "HW-FILES", stopping);
                Assert.Equal("failed", outcome);
                Assert.EndsWith("kayit.bin", path);
                return detail;
            }
            var (pushOutcome, pushPath, pushDetail) = await Transfer(idle).PushAsync(Push(), "HW-FILES", new DateTime(2026, 10, 22), stopping);
            Assert.Equal(("failed", null), (pushOutcome, pushPath));
            Assert.Empty(Directory.GetFiles(_agent.Paths.SecureDir, "transfer-*"));   // yarım dosya kalmaz
            return pushDetail;
        }

        // Sunucu bağlanılamadan ya da yanıt vermeden hata: SocketsHttpHandler'ın fırlattığı biçimde
        private void Fails(Func<Exception> error) =>
            _server = (_, _) => throw (_thrown = error());

        // Karşı taraf bağlantıyı sıfırladı: NetworkStream'in istisnası (iletisi soketinkini içerir; soketin iletisi sistemin
        // dilindedir)
        private static Exception ConnectionReset(bool write = true)
        {
            var socket = new SocketException((int)SocketError.ConnectionReset);
            return new IOException($"Unable to {(write ? "write data to" : "read data from")} the transport connection: {socket.Message}.", socket);
        }

        private static string Innermost(Exception error)
        {
            while (error.InnerException != null) error = error.InnerException;
            return error.Message;
        }

        // ------------------------------------------------------------------ alma (sunucuya yükleme)
        // Saha: vekil gövdeyi yarıda kesti. Sunucu gövdenin iki parçasını alır, sonra soket kapanır; StreamContent'in kopyası
        // .NET'in kendi istisnasıyla biter (HttpRequestException "Error while copying content to a stream.", içinde IOException
        // ve SocketException). Açıklama bağlantının kesildiğini ve vekilin boyut sınırını söyler, ham ileti parantez içinde.
        [Fact]
        public async Task Pull_UploadCutMidway_SaysTheConnectionWasCut_AndNamesTheProxyLimit()
        {
            var sink = new CuttingSink(2, () => ConnectionReset());
            Exception copy = null;
            _server = async (request, token) =>
            {
                try { await request.Content.CopyToAsync(sink, token); }
                catch (Exception ex) { copy = ex; throw; }
                return new HttpResponseMessage(HttpStatusCode.OK);
            };
            string detail = await Run(upload: true);

            // Sahadaki istisna: .NET kopyayı sardı (bilinmeyen HttpRequestError), neden içteki soket hatası
            var wrapped = Assert.IsType<HttpRequestException>(copy);
            Assert.Equal(HttpRequestError.Unknown, wrapped.HttpRequestError);
            Assert.Same(sink.Thrown, wrapped.InnerException);
            Assert.Equal($"{UploadCut} ({wrapped.Message} / {Innermost(wrapped)})", detail);
            Assert.StartsWith("bağlantı kesildi", detail);
            Assert.Contains("vekil", detail);
            Assert.Contains("(" + wrapped.Message, detail);
        }

        // Neden metinden değil türden: Türkçe iletili ConnectionAborted da kesilen bağlantıdır; "Error while copying content to a
        // stream." iletili ama içinde soket hatası olmayan (yerel dosya okunamadı) hata kesilen bağlantı değildir, vekil anılmaz
        [Fact]
        public async Task Pull_TheCauseComesFromTheType_NotFromTheText()
        {
            Fails(() => new HttpRequestException("İçerik bir akışa kopyalanırken hata oluştu.",
                new IOException("Aktarım bağlantısına veri yazılamıyor.", new SocketException((int)SocketError.ConnectionAborted))));
            string aborted = await Run(upload: true);
            Assert.Equal($"{UploadCut} (İçerik bir akışa kopyalanırken hata oluştu. / {Innermost(_thrown)})", aborted);

            Fails(() => new HttpRequestException("Error while copying content to a stream.",
                new IOException("The process cannot access the file because another process has locked a portion of the file.")));
            string local = await Run(upload: true);
            Assert.Equal("yükleme tamamlanmadı (Error while copying content to a stream. / The process cannot access the file because "
                + "another process has locked a portion of the file.)", local);
            Assert.DoesNotContain("vekil", local);
        }

        // Cloudflare (Free/Pro) 100 MB'tan büyük gövdeyi 413 ile reddeder; gövde okunmadan
        [Fact]
        public async Task Pull_413_SaysTheFileIsTooLarge()
        {
            _server = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));
            Assert.Equal("dosya çok büyük; sunucu ya da önündeki vekil kabul etmedi (HTTP 413)", await Run(upload: true));
        }

        // ------------------------------------------------------------------ gönderme (sunucudan indirme)
        // İndirme yarıda kesildi: soket kapandı (RST) ya da yanıt bildirilen boyuttan önce bitti (HttpIOException ResponseEnded).
        // Boyut sınırı burada olası değil: anılmaz.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Push_DownloadCutMidway_SaysTheConnectionWasCut(bool reset)
        {
            var body = new CuttingBody(3, () => reset
                ? ConnectionReset(write: false)
                : new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely, with at least 7000 additional bytes expected."));
            _server = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
            string detail = await Run(upload: false);
            Assert.Equal($"{DownloadCut} ({body.Thrown.Message})", detail);
            Assert.DoesNotContain("boyut sınırı", detail);
            Assert.Empty(Directory.GetFiles(Path.Combine(_agent.Paths.DataDir, "inbox", "2026-10-22")));
        }

        // ------------------------------------------------------------------ iki yön
        // Başka durum kodu olduğu gibi; 413 yalnızca yüklemede boyut sınırıdır
        [Theory]
        [InlineData(true, 500, "yüklenemedi: HTTP 500")]
        [InlineData(true, 502, "yüklenemedi: HTTP 502")]
        [InlineData(false, 500, "indirilemedi: HTTP 500")]
        [InlineData(false, 404, "indirilemedi: HTTP 404")]
        [InlineData(false, 413, "indirilemedi: HTTP 413")]
        public async Task OtherStatus_KeepsTheCode(bool upload, int code, string expected)
        {
            _server = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code));
            Assert.Equal(expected, await Run(upload));
        }

        // Bağlanırken: SocketsHttpHandler iletiye "(ad:port)" ekler, HttpRequestError'ı türden çıkarır (ad bulunamadı:
        // NameResolutionError; DNS sunucusu yanıt vermedi, TryAgain: ConnectionError). Vekil anılmaz.
        [Theory]
        [InlineData(true, HttpRequestError.NameResolutionError, SocketError.HostNotFound, "sunucunun adı çözülemedi: DNS yanıt vermedi ya da ad bulunamadı")]
        [InlineData(false, HttpRequestError.NameResolutionError, SocketError.HostNotFound, "sunucunun adı çözülemedi: DNS yanıt vermedi ya da ad bulunamadı")]
        [InlineData(true, HttpRequestError.ConnectionError, SocketError.TryAgain, "sunucunun adı çözülemedi: DNS yanıt vermedi ya da ad bulunamadı")]
        [InlineData(true, HttpRequestError.ConnectionError, SocketError.ConnectionRefused, "sunucuya bağlanılamadı: sunucu kapalı ya da ağdan erişilemiyor")]
        [InlineData(false, HttpRequestError.ConnectionError, SocketError.ConnectionRefused, "sunucuya bağlanılamadı: sunucu kapalı ya da ağdan erişilemiyor")]
        [InlineData(false, HttpRequestError.ConnectionError, SocketError.TimedOut, "sunucuya bağlanılamadı: sunucu kapalı ya da ağdan erişilemiyor")]
        // Bağlanırken sıfırlanan bağlantı bağlanamamaktır, kesilen aktarım değil
        [InlineData(true, HttpRequestError.ConnectionError, SocketError.ConnectionReset, "sunucuya bağlanılamadı: sunucu kapalı ya da ağdan erişilemiyor")]
        public async Task NameResolutionAndConnectFailures(bool upload, HttpRequestError error, SocketError socket, string expected)
        {
            Fails(() =>
            {
                var inner = new SocketException((int)socket);
                return new HttpRequestException(error, $"{inner.Message} (pops.example:443)", inner);
            });
            string detail = await Run(upload);
            Assert.Equal($"{expected} ({_thrown.Message})", detail);
            Assert.DoesNotContain("vekil", detail);
        }

        // Sunucu sertifikası reddedildi (ServerTrust'ın denetimi): ham iletide "see inner exception", içteki ileti de yazılır
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TlsFailure_SaysTheServerCertificateWasRejected(bool upload)
        {
            Fails(() => new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.",
                new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")));
            string detail = await Run(upload);
            Assert.Equal("güvenli bağlantı kurulamadı: sunucu sertifikası reddedildi ya da TLS el sıkışması başarısız (The SSL connection could "
                + "not be established, see inner exception. / The remote certificate was rejected by the provided RemoteCertificateValidationCallback.)", detail);
            Assert.DoesNotContain("vekil", detail);
        }

        // Süre dolunca soket kapanır ve hata bir bağlantı hatası olarak da gelebilir: açıklama yine sürenin eski metnidir
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Timeout_KeepsItsText_EvenWhenTheSocketErrorComesBack(bool upload)
        {
            if (upload)
                _server = async (request, token) =>
                {
                    await request.Content.CopyToAsync(new CuttingSink(1, () => ConnectionReset(), stall: true), token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                };
            else
            {
                var body = new CuttingBody(1, () => ConnectionReset(write: false), stall: true);
                _server = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
            }
            Assert.Equal("zaman aşımı: 0.3 saniyedir veri yok", await Run(upload, TimeSpan.FromMilliseconds(300)));
        }

        // Servis duruyor: açıklama eskisi gibi hatanın kendi iletisidir (süre ya da bağlantı açıklaması yok)
        [Fact]
        public async Task ServiceStopping_KeepsTheErrorAsItIs()
        {
            var body = new CuttingBody(1, null, stall: true);
            _server = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
            using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            string detail = await Run(upload: false, stopping: stopping.Token);
            Assert.IsAssignableFrom<OperationCanceledException>(body.Thrown);
            Assert.Equal(body.Thrown.Message, detail);

            // Servis dururken gelen bağlantı hatası da olduğu gibi; servis çalışırken aynı hata açıklanır
            var cut = new HttpRequestException("Error while copying content to a stream.", ConnectionReset());
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();
            using (var watchdog = new TransferWatchdog(Long, Long, stopped.Token))
                Assert.Equal("Error while copying content to a stream.", watchdog.Detail(cut, upload: true));
            using (var watchdog = new TransferWatchdog(Long, Long, CancellationToken.None))
                Assert.Equal($"{UploadCut} (Error while copying content to a stream. / {Innermost(cut)})", watchdog.Detail(cut, upload: true));
        }

        // ------------------------------------------------------------------ file_result
        // Uzun ham ileti açıklamanın sınırına (300) sığar, parantez kapanır; Result onu bir daha kısaltmaz
        [Fact]
        public void LongRawError_FitsTheResultLimit()
        {
            string detail = TransferError.Describe(new HttpRequestException(new string('x', 400), ConnectionReset()), upload: true);
            Assert.Equal(FileTransfer.MaxReason, detail.Length);
            Assert.StartsWith(UploadCut + " (xxx", detail);
            Assert.EndsWith("x…)", detail);
            Assert.Equal(detail, FileTransfer.Result(PullId, "failed", "C:\\a.bin", detail)["detail"]);
        }

        // Servisin yolu: file_result'ın biçimi aynı (outcome "failed", açıklama metni), açıklama Türkçe neden ve ham ileti
        [Fact]
        public async Task Worker_ReportsTheReason_InTheSameFileResult()
        {
            var sent = new List<JsonElement>();
            using var worker = new Worker(NullLogger<Worker>.Instance, _agent.Context)
            {
                HwId = "HW-FILES",
                SendOverride = p => { lock (sent) sent.Add(JsonSerializer.SerializeToElement(p)); return Task.FromResult(true); },
                TrayOverride = _ => { },
            };
            _server = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));
            string file = PullFile(5000);
            await worker.HandleFileTransferAsync("file_pull", JsonSerializer.SerializeToElement(PullCommand(file)), CancellationToken.None);
            await worker.FileTransferTask;
            JsonElement result;
            lock (sent) result = Assert.Single(sent);
            Assert.Equal(new[] { "type", "transfer_id", "outcome", "path", "detail" }, result.EnumerateObject().Select(p => p.Name));
            Assert.Equal(("file_result", PullId, "failed", "dosya çok büyük; sunucu ya da önündeki vekil kabul etmedi (HTTP 413)"),
                (result.GetProperty("type").GetString(), result.GetProperty("transfer_id").GetString(), result.GetProperty("outcome").GetString(),
                    result.GetProperty("detail").GetString()));
        }
    }
}
