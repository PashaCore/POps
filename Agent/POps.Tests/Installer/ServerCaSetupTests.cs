using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using POps.Installer;
using Xunit;

namespace POps.Tests.Installer
{
    // MSI SERVER_CA_CERT: PEM yolu -> C:\POpsData\secure\server-ca.pem (yalnızca SYSTEM/Administrators); verilmezse
    // mevcut dosya korunur; "system" siler; bozuk ya da CA olmayan sertifika kurulumu durdurur.
    [Collection(SharedStateCollection.Name)]
    public class ServerCaSetupTests : SharedStateTestBase
    {
        private readonly string _root = TestEnvironment.NewDir("msi-ca");
        private readonly List<string> _log = new List<string>();

        public ServerCaSetupTests() => Setup.TrustedBaseForTests = _root;

        private Layout NewLayout() => new Layout
        {
            DataDir = Path.Combine(_root, "POpsData"),
            SecureDir = Path.Combine(_root, "POpsData", "secure"),
            LogDir = Path.Combine(_root, "POpsLogs"),
            LegacyDirs = new string[0],
        };

        private string Configure(Layout layout, params (string Key, string Value)[] properties)
        {
            string installDir = Path.Combine(_root, "PF", "POps");
            var data = new Dictionary<string, string>
            {
                ["INSTALLFOLDER"] = installDir + "\\",
                ["INSTALLDIR_STATE"] = Directory.Exists(installDir) ? Setup.StatePops : Setup.StateNew,
                ["SERVER_URL"] = "https://pops.okul.local",
            };
            foreach (var (key, value) in properties) data[key] = value;
            return Setup.Configure(data, layout, _log.Add);
        }

        private static string StorePath(Layout layout) => Path.Combine(layout.SecureDir, Setup.ServerCaFile);

        private string PemFile(string name, X509Certificate2 cert)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, "# okul CA\r\n-----BEGIN CERTIFICATE-----\r\n" + Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks) + "\r\n-----END CERTIFICATE-----\r\n");
            return path;
        }

        private static X509Certificate2 NewCertificate(string subject, bool ca)
        {
            using (RSA key = RSA.Create(2048))
            {
                var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
                return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            }
        }

        private static bool LockedDown(FileSystemSecurity sec) =>
            sec.AreAccessRulesProtected && sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().All(r =>
                r.IdentityReference.Equals(Setup.SystemSid) || r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));

        [Fact]
        public void Path_WritesTheCertificateLockedDown()
        {
            Layout layout = NewLayout();
            X509Certificate2 ca = NewCertificate("CN=POps Okul CA", true);
            Assert.Null(Configure(layout, ("SERVER_CA_CERT", PemFile("pops-ca.pem", ca))));

            string stored = File.ReadAllText(StorePath(layout));
            Assert.StartsWith("-----BEGIN CERTIFICATE-----", stored);
            Assert.Equal(ca.Thumbprint, new X509Certificate2(File.ReadAllBytes(StorePath(layout))).Thumbprint);
            Assert.True(LockedDown(File.GetAccessControl(StorePath(layout))));
            Assert.Contains(_log, l => l.Contains("kurum sertifikası yazıldı") && l.Contains("POps Okul CA"));
        }

        [Fact]
        public void Absent_KeepsTheExistingCertificate()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_CA_CERT", PemFile("pops-ca.pem", NewCertificate("CN=POps Okul CA", true)))));
            string before = File.ReadAllText(StorePath(layout));
            Assert.Null(Configure(layout));
            Assert.Null(Configure(layout, ("SERVER_CA_CERT", "")));
            Assert.Equal(before, File.ReadAllText(StorePath(layout)));
        }

        [Fact]
        public void System_RemovesTheCertificate()
        {
            Layout layout = NewLayout();
            Assert.Null(Configure(layout, ("SERVER_CA_CERT", PemFile("pops-ca.pem", NewCertificate("CN=POps Okul CA", true)))));
            Assert.Null(Configure(layout, ("SERVER_CA_CERT", "System")));
            Assert.False(File.Exists(StorePath(layout)));
            Assert.Contains(_log, l => l.Contains("kurum sertifikası kaldırıldı"));
            // Dosya yokken de hata değildir
            Assert.Null(Configure(layout, ("SERVER_CA_CERT", "system")));
        }

        [Fact]
        public void BrokenOrMissingFile_StopsTheInstallBeforeWritingAnything()
        {
            Layout layout = NewLayout();
            string bad = Path.Combine(_root, "bad.pem");
            File.WriteAllText(bad, "bu bir sertifika degil");
            string error = Configure(layout, ("SERVER_CA_CERT", bad));
            Assert.Contains("SERVER_CA_CERT", error);
            Assert.Contains("PEM", error);
            Assert.False(Directory.Exists(layout.SecureDir));

            File.WriteAllText(bad, "-----BEGIN CERTIFICATE-----\r\nAAAA\r\n-----END CERTIFICATE-----\r\n");
            Assert.Contains("SERVER_CA_CERT geçerli bir PEM sertifikası değil", Configure(layout, ("SERVER_CA_CERT", bad)));
            Assert.Contains("bulunamadı", Configure(layout, ("SERVER_CA_CERT", Path.Combine(_root, "yok.pem"))));
        }

        [Fact]
        public void ServerCertificateInsteadOfCa_IsRefused()
        {
            Layout layout = NewLayout();
            string error = Configure(layout, ("SERVER_CA_CERT", PemFile("server.pem", NewCertificate("CN=pops.okul.local", false))));
            Assert.Contains("CA sertifikası değil", error);
            Assert.False(File.Exists(StorePath(layout)));
        }
    }
}
