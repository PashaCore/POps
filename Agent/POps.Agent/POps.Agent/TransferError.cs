using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace POpsAgent
{
    // Dosya aktarımının "failed" açıklaması (bkz. FileTransfer): yöneticinin anlayacağı Türkçe neden, parantez içinde .NET'in
    // ham iletisi (tanı için; en içteki hatanın iletisi üsttekinde geçmiyorsa " / " ile o da). Neden istisnanın türünden,
    // HttpRequestError'dan, SocketError'dan ve durum kodundan çıkar, iletinin metninden değil: iletiler sistemin diline ve .NET
    // sürümüne göre değişir. Vekilin boyut sınırı yalnızca olası olduğu yerde anılır (yüklemede kesilen bağlantı, HTTP 413).
    // Süre dolması ve servisin durması çağıranındır (bkz. TransferWatchdog.Detail). Açıklama file_result'ın sınırına sığar.
    internal static class TransferError
    {
        // Sıra öncelik sırasıdır: zincirde birden çok neden varsa büyük olan söylenir (ör. bağlanırken DNS sunucusu yanıt
        // vermezse SocketsHttpHandler ConnectionError verir, içindeki SocketError.TryAgain ad çözmedir)
        internal enum Kind { Other, Cut, Connect, NameResolution, Secure }

        internal const string UploadCut = "bağlantı kesildi: sunucu ya da önündeki vekil sunucu yüklemeyi yarıda kesti; olası neden "
            + "vekilin boyut sınırı (ör. Cloudflare 100 MB)";
        internal const string DownloadCut = "bağlantı kesildi: indirme yarıda kaldı; sunucu ya da aradaki bir vekil sunucu bağlantıyı kapattı";
        internal const string NameResolution = "sunucunun adı çözülemedi: DNS yanıt vermedi ya da ad bulunamadı";
        internal const string Connect = "sunucuya bağlanılamadı: sunucu kapalı ya da ağdan erişilemiyor";
        internal const string Secure = "güvenli bağlantı kurulamadı: sunucu sertifikası reddedildi ya da TLS el sıkışması başarısız";
        internal const string UploadOther = "yükleme tamamlanmadı", DownloadOther = "indirme tamamlanmadı";
        internal const string TooLarge = "dosya çok büyük; sunucu ya da önündeki vekil kabul etmedi (HTTP 413)";

        // upload: dosya alma (PC -> sunucu); değilse dosya gönderme (sunucudan indirme)
        internal static string Describe(Exception error, bool upload)
        {
            ArgumentNullException.ThrowIfNull(error);
            string text = Classify(error) switch
            {
                Kind.Cut => upload ? UploadCut : DownloadCut,
                Kind.Connect => Connect,
                Kind.NameResolution => NameResolution,
                Kind.Secure => Secure,
                _ => upload ? UploadOther : DownloadOther,
            };
            // " (" + ham ileti + ")" sınıra sığar; sığmayan ham ileti "…" ile kısalır
            string raw = Raw(error);
            int room = FileTransfer.MaxReason - text.Length - 3;
            if (raw.Length > room) raw = string.Concat(raw.AsSpan(0, Math.Max(0, room - 1)), "…");
            return $"{text} ({raw})";
        }

        // Başarısız HTTP yanıtı: yüklemede 413 sunucunun ya da vekilin boyut sınırıdır; başka her kod olduğu gibi söylenir
        internal static string Status(int code, bool upload) =>
            upload && code == 413 ? TooLarge : $"{(upload ? "yüklenemedi" : "indirilemedi")}: HTTP {code}";

        // Zincirdeki en öncelikli neden; hiçbiri değilse Other (yerel disk ya da dosya hatası da: zincirinde soket hatası yok)
        internal static Kind Classify(Exception error)
        {
            Kind found = Kind.Other;
            for (Exception? e = error; e != null; e = e.InnerException)
            {
                Kind kind = e switch
                {
                    AuthenticationException => Kind.Secure,
                    HttpRequestException http => Of(http.HttpRequestError),
                    HttpIOException io => Of(io.HttpRequestError),
                    SocketException socket => Of(socket.SocketErrorCode),
                    _ => Kind.Other,
                };
                if (kind > found) found = kind;
            }
            return found;
        }

        // ConnectionError ve ProxyTunnelError yalnızca bağlantı kurulurken; ResponseEnded: yanıt bitmeden bağlantı kapandı
        private static Kind Of(HttpRequestError error) => error switch
        {
            HttpRequestError.SecureConnectionError => Kind.Secure,
            HttpRequestError.NameResolutionError => Kind.NameResolution,
            HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError => Kind.Connect,
            HttpRequestError.ResponseEnded => Kind.Cut,
            _ => Kind.Other,
        };

        // Kurulmuş bağlantıyı karşı taraf (sunucu ya da vekil) kesti: ConnectionReset (RST), ConnectionAborted (Windows'ta karşı
        // taraf yazılırken kapatınca da), Shutdown, NotConnected. Ağ yolunun kopması (TimedOut, NetworkDown ...) kesmeyi karşı
        // tarafa yüklemesin diye burada yok (Other).
        private static Kind Of(SocketError error) => error switch
        {
            SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoRecovery or SocketError.NoData => Kind.NameResolution,
            SocketError.ConnectionRefused => Kind.Connect,
            SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown or SocketError.NotConnected => Kind.Cut,
            _ => Kind.Other,
        };

        // Üst istisnanın iletisi; en içteki istisnanın iletisi onda geçmiyorsa (ör. "see inner exception") o da
        internal static string Raw(Exception error)
        {
            Exception inner = error;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner == error || error.Message.Contains(inner.Message, StringComparison.Ordinal) ? error.Message : $"{error.Message} / {inner.Message}";
        }
    }
}
