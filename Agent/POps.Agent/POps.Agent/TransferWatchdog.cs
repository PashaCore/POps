using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Dosya aktarımının süreleri (bkz. FileTransfer). İstemcinin kendi süresi yoktur (AgentHttp.TransferClient); veri aktığı
    // sürece aktarım kesilmez. Token şunlardan biri olunca iptal olur:
    //  * boşta kalma: veri akmadan geçen süre (servis: 2 dk). Her okumada ya da yazmada yeniden başlar (Progress);
    //    bağlantı ve yanıt başlığı beklenirken de işler.
    //  * toplam süre (servis: 2 saat), veri aksa da.
    //  * servis duruyor (çağıranın jetonu).
    // Expired, aktarımın bir süre dolduğu için kesildiğini file_result'ın açıklamasıyla söyler; Detail başka hataları da
    // açıklar (bkz. TransferError).
    internal sealed class TransferWatchdog : IDisposable
    {
        private readonly TimeSpan _idleTimeout;
        private readonly TimeSpan _totalTimeout;
        private readonly CancellationToken _stopping;
        private readonly CancellationTokenSource _idle = new CancellationTokenSource();
        private readonly CancellationTokenSource _total = new CancellationTokenSource();
        private readonly CancellationTokenSource _any;
        private int _disposed;

        public TransferWatchdog(TimeSpan idle, TimeSpan total, CancellationToken stopping)
        {
            _idleTimeout = idle;
            _totalTimeout = total;
            _stopping = stopping;
            _any = CancellationTokenSource.CreateLinkedTokenSource(stopping, _idle.Token, _total.Token);
            _total.CancelAfter(total);
            _idle.CancelAfter(idle);
        }

        // İsteğe ve akışa verilen jeton
        public CancellationToken Token => _any.Token;

        // Veri aktı: boşta kalma süresi yeniden başlar (süre dolduysa bir şey değişmez)
        public void Progress()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _idle.CancelAfter(_idleTimeout); }
            catch (ObjectDisposedException) { }
        }

        // Aktarım bir süre dolduğu için kesildiyse açıklaması; değilse null (servis duruyor, ağ ya da disk hatası)
        public string? Expired =>
            _stopping.IsCancellationRequested ? null
            : _total.IsCancellationRequested ? TotalMessage(_totalTimeout)
            : _idle.IsCancellationRequested ? IdleMessage(_idleTimeout)
            : null;

        // Aktarımı kesen hatanın file_result açıklaması: süre dolduysa süre; servis duruyorsa hatanın kendi iletisi (eskisi gibi);
        // değilse Türkçe neden ve parantez içinde ham ileti (upload: dosya alma, değilse gönderme)
        public string Detail(Exception error, bool upload) =>
            Expired ?? (_stopping.IsCancellationRequested ? error.Message : TransferError.Describe(error, upload));

        // Servis: "zaman aşımı: 2 dakikadır veri yok", "toplam süre 2 saati aştı"
        internal static string IdleMessage(TimeSpan idle) =>
            "zaman aşımı: " + (WholeMinutes(idle) ? $"{Number(idle.TotalMinutes)} dakikadır" : $"{Number(idle.TotalSeconds)} saniyedir") + " veri yok";

        internal static string TotalMessage(TimeSpan total) =>
            "toplam süre " + (total >= TimeSpan.FromHours(1) && total.Ticks % TimeSpan.TicksPerHour == 0 ? $"{Number(total.TotalHours)} saati"
                : WholeMinutes(total) ? $"{Number(total.TotalMinutes)} dakikayı"
                : $"{Number(total.TotalSeconds)} saniyeyi") + " aştı";

        private static bool WholeMinutes(TimeSpan value) => value >= TimeSpan.FromMinutes(1) && value.Ticks % TimeSpan.TicksPerMinute == 0;

        private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _any.Dispose();
            _idle.Dispose();
            _total.Dispose();
        }
    }

    // Yüklenen dosyanın akışı (bkz. FileTransfer.PullAsync): StreamContent bir parça okur, sunucuya yazar, sonra yine okur;
    // her okuma bir önceki parçanın gittiğini söyler (progress). Konum ve boyut dosyanınkidir (yeniden gönderimde başa sarılır);
    // kapatılınca dosya da kapanır (StreamContent'in dosyayı kapatması gibi).
    internal sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action _progress;

        public ProgressStream(Stream inner, Action progress)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _progress();
            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            _progress();
            return _inner.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _progress();
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _progress();
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
