using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Kurum sertifikasına güven: server-ca.pem varsa sunucu sertifikası yalnızca o CA'ya zincirlenir ve ana makine adı
    // eşleşirse kabul edilir; dosya yoksa sistem deposu (.NET'in kararı) geçerlidir; bozuk dosya her şeyi reddeder.
    public class ServerTrustTests : TestBase, IDisposable
    {
        private const string Host = "pops.okul.local";
        private readonly string _caPath;
        private readonly X509Certificate2 _ca = NewCa("CN=POps Test CA");
        private readonly X509Certificate2 _otherCa = NewCa("CN=Baska Kurum CA");
        private readonly X509Certificate2 _server;

        public ServerTrustTests()
        {
            _caPath = Path.Combine(TestEnvironment.NewDir("tls"), ServerTrust.FileName);
            _server = NewServerCertificate(_ca, Host);
            ServerTrust.CaPath = _caPath;
            ServerTrust.Reload();
        }

        public void Dispose()
        {
            ServerTrust.CaPath = Path.Combine(TestEnvironment.DefaultSecureDir, ServerTrust.FileName);
            ServerTrust.Reload();
            _ca.Dispose(); _otherCa.Dispose(); _server.Dispose();
        }

        private void PinTo(X509Certificate2 ca)
        {
            File.WriteAllText(_caPath, ca.ExportCertificatePem());
            ServerTrust.Reload();
        }

        // Sistem deposu bu sertifikayı tanımaz: .NET zincir hatası bildirir
        private static bool Check(X509Certificate2 cert, string host, SslPolicyErrors errors = SslPolicyErrors.RemoteCertificateChainErrors) =>
            ServerTrust.Validate(cert, null, errors, host);

        [Fact]
        public void CorrectCa_IsAccepted_EvenThoughTheSystemStoreRejectsIt()
        {
            PinTo(_ca);
            Assert.Equal("custom", ServerTrust.Mode);
            Assert.True(Check(_server, Host));
            Assert.True(Check(_server, Host.ToUpperInvariant()));
        }

        [Fact]
        public void OtherCa_IsRejected_EvenIfTheSystemStoreTrustsIt()
        {
            PinTo(_otherCa);
            Assert.False(Check(_server, Host));
            Assert.False(Check(_server, Host, SslPolicyErrors.None));   // sistem deposu (ör. Let's Encrypt) sayılmaz
        }

        [Fact]
        public void HostMismatch_IsRejected()
        {
            PinTo(_ca);
            Assert.False(Check(_server, "baska.okul.local"));
            Assert.False(Check(_server, null));
        }

        [Fact]
        public void NoFile_KeepsTheSystemDecision()
        {
            Assert.False(File.Exists(_caPath));
            Assert.Equal("system", ServerTrust.Mode);
            Assert.True(Check(_server, Host, SslPolicyErrors.None));
            Assert.False(Check(_server, Host, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(Check(_server, Host, SslPolicyErrors.RemoteCertificateNameMismatch));
        }

        [Fact]
        public void BrokenFile_RejectsEverything()
        {
            File.WriteAllText(_caPath, "bu bir sertifika degil");
            ServerTrust.Reload();
            Assert.Equal("custom", ServerTrust.Mode);
            Assert.False(Check(_server, Host, SslPolicyErrors.None));
            Assert.False(Check(null, Host, SslPolicyErrors.None));
        }

        [Fact]
        public void NoCertificate_IsRejected()
        {
            PinTo(_ca);
            Assert.False(Check(null, Host));
        }

        [Fact]
        public void Callbacks_TakeTheHostFromTheAddress()
        {
            PinTo(_ca);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/api/x");
            Assert.True(ServerTrust.HttpCallback(request, _server, null, SslPolicyErrors.RemoteCertificateChainErrors));
            using var other = new HttpRequestMessage(HttpMethod.Get, "https://baska.okul.local/api/x");
            Assert.False(ServerTrust.HttpCallback(other, _server, null, SslPolicyErrors.RemoteCertificateChainErrors));

            Assert.True(ServerTrust.WebSocketCallback(new Uri($"wss://{Host}/ws/agent/HW-1"))(null, _server, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(ServerTrust.WebSocketCallback(new Uri("wss://baska.okul.local/ws/agent/HW-1"))(null, _server, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }

        // Ajanın bütün HTTP istemcileri yönlendirme izlemez ve bu doğrulamayı kullanır
        [Fact]
        public void Handler_UsesTheValidationAndNoRedirects()
        {
            using HttpClientHandler handler = ServerTrust.NewHandler();
            Assert.False(handler.AllowAutoRedirect);
            Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
            Assert.NotNull(AgentHttp.Handler.ServerCertificateCustomValidationCallback);
        }

        [Fact]
        public void CapabilitiesMessage_ReportsTheMode()
        {
            Assert.Contains("\"server_ca\":\"system\"", JsonSerializer.Serialize(AgentCapabilities.StatusMessage()));
            PinTo(_ca);
            Assert.Contains("\"server_ca\":\"custom\"", JsonSerializer.Serialize(AgentCapabilities.StatusMessage()));
        }

        // ------------------------------------------------------------------ test sertifikaları

        private static X509Certificate2 NewCa(string subject)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        }

        private static X509Certificate2 NewServerCertificate(X509Certificate2 ca, string host)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            request.CertificateExtensions.Add(san.Build());
            return request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 1, 2, 3, 4 });
        }
    }
}
