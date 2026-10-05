#nullable disable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace POps.Shared
{
    // Sunucu sertifikasına güven: kurum sertifikası (özel CA). Ajanın sunucuya giden HER bağlantısı (komut ve Vision
    // WebSocket'leri, ajan HTTP uçları, /updates paket indirme) sertifikayı yalnızca burada doğrular.
    //  * C:\POpsData\secure\server-ca.pem (MSI SERVER_CA_CERT ile yazılır; yalnızca SYSTEM/Administrators) VARSA:
    //    sunucu sertifikası yalnızca bu tek köke zincirlenirse (CustomRootTrust; iptal denetimi yok, çevrimdışı okul
    //    ağında CRL erişimi yoktur) VE ana makine adı sertifikayla eşleşirse kabul edilir. Sistem deposundaki hiçbir
    //    kök geçerli sayılmaz: sistem deposuna sokulmuş bir kökle sunucu taklit edilemez.
    //  * Dosya YOKSA bugünkü davranış: işletim sisteminin güven deposu (ör. Let's Encrypt).
    //  * Dosya var ama okunamıyor/bozuksa güvenli yöne düşülür: hiçbir sertifika kabul edilmez (kurulum bozuk PEM'i
    //    zaten reddeder; buraya yalnızca elle bozulmuş dosya düşer).
    // Reddedilen bağlantı kurulmaz; ajan geri çekilmeyle yeniden dener (bkz. ReconnectBackoff).
    [SupportedOSPlatform("windows")]
    public static class ServerTrust
    {
        public const string FileName = "server-ca.pem";
        public const string ModeCustom = "custom", ModeSystem = "system";
        public const string ChainMessage = "[GÜVENLİK] Sunucu sertifikası kurum sertifikasına (server-ca.pem) zincirlenmiyor";

        private static readonly object Gate = new object();
        private static string _loadedPath;
        private static X509Certificate2 _ca;
        private static bool _broken;
        private static DateTime _lastRejectLogUtc = DateTime.MinValue;

        // Veri klasörü (DataDirectory) seçilince servis açılışta ayarlar; testlerde geçici dosyaya çevrilir
        public static string CaPath { get; set; } = Path.Combine(FolderSettings.DefaultDataDirectory, "secure", FileName);

        // Sunucuya bildirilen kip ("server_ca"): dosya varsa (bozuk olsa da) custom, yoksa system
        public static string Mode
        {
            get { lock (Gate) { EnsureLoaded(); return _ca != null || _broken ? ModeCustom : ModeSystem; } }
        }

        // Dosya değiştiyse (kurulum) yeniden okunur; açılışta kipi loglamak için de çağrılır
        public static void Reload()
        {
            lock (Gate)
            {
                _loadedPath = null;
                EnsureLoaded();
                if (_ca != null) POpsHelpers.Log("TLS", $"Sunucu sertifikası kurum sertifikasıyla doğrulanacak: {_ca.Subject} (parmak izi {_ca.Thumbprint}).");
                else if (_broken) POpsHelpers.Log("TLS", $"[GÜVENLİK] {CaPath} okunamadı ya da geçerli bir sertifika değil; sunucuya hiçbir bağlantı kabul edilmeyecek. Dosyayı düzeltin ya da silin (sistem deposu).", true);
                else POpsHelpers.Log("TLS", "Sunucu sertifikası işletim sisteminin güven deposuyla doğrulanacak (server-ca.pem yok).");
            }
        }

        private static void EnsureLoaded()
        {
            if (_loadedPath == CaPath) return;
            _ca?.Dispose();
            _ca = null;
            _broken = false;
            _loadedPath = CaPath;
            try
            {
                if (!File.Exists(CaPath)) return;
                _ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaPath));
            }
            catch (Exception)
            {
                _ca = null;
                _broken = true;
            }
        }

        // Tek doğrulama. host: bağlanılan ana makine adı (URI'nin IdnHost'u). errors: .NET'in sistem deposuyla vardığı sonuç.
        public static bool Validate(X509Certificate2 certificate, X509Chain presented, SslPolicyErrors errors, string host)
        {
            X509Certificate2 ca;
            bool broken;
            lock (Gate)
            {
                EnsureLoaded();
                ca = _ca;
                broken = _broken;
            }
            if (ca == null && !broken) return errors == SslPolicyErrors.None;   // sistem deposu, bugünkü davranış
            if (broken) return Reject($"[GÜVENLİK] {FileName} bozuk; sunucu sertifikası doğrulanamadı, bağlantı kurulmadı.");
            if (certificate == null) return Reject(ChainMessage + " (sertifika yok).");

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            // Sunucunun gönderdiği ara sertifikalar (varsa) zincir kurulurken kullanılır
            if (presented != null)
                foreach (X509ChainElement element in presented.ChainElements)
                    if (!element.Certificate.Equals(certificate)) chain.ChainPolicy.ExtraStore.Add(element.Certificate);

            bool chained;
            try { chained = chain.Build(certificate); }
            catch (Exception) { chained = false; }
            if (chained)
            {
                // CustomRootTrust yalnızca verilen köke izin verir; yine de kökün bizim CA olduğu ayrıca denetlenir
                X509Certificate2 root = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;
                chained = root.RawData.AsSpan().SequenceEqual(ca.RawData);
            }
            if (!chained) return Reject($"{ChainMessage}: {Describe(certificate)}.");
            if (string.IsNullOrEmpty(host) || !certificate.MatchesHostname(host))
                return Reject($"[GÜVENLİK] Sunucu sertifikası ana makine adıyla eşleşmiyor ({host}): {Describe(certificate)}.");
            return true;
        }

        // HttpClientHandler.ServerCertificateCustomValidationCallback
        public static bool HttpCallback(HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors) =>
            Validate(certificate, chain, errors, request?.RequestUri?.IdnHost);

        // ClientWebSocketOptions.RemoteCertificateValidationCallback: bağlanılan adres kapanışta taşınır
        public static RemoteCertificateValidationCallback WebSocketCallback(Uri uri) =>
            (sender, certificate, chain, errors) => Validate(certificate as X509Certificate2 ?? (certificate == null ? null : new X509Certificate2(certificate)), chain, errors, uri?.IdnHost);

        public static HttpClientHandler NewHandler() =>
            new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = HttpCallback };

        private static string Describe(X509Certificate2 certificate)
        {
            try { return $"konu {LogText.Safe(certificate.Subject, 120)}, veren {LogText.Safe(certificate.Issuer, 120)}"; }
            catch { return "(okunamadı)"; }
        }

        // Aynı sebep her denemede loglanmasın: dakikada bir
        private static bool Reject(string message)
        {
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                if (now - _lastRejectLogUtc >= TimeSpan.FromMinutes(1))
                {
                    _lastRejectLogUtc = now;
                    POpsHelpers.Log("TLS", message, true);
                }
            }
            return false;
        }
    }
}
