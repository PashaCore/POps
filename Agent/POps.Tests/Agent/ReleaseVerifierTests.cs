using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    public class ReleaseVerifierTests : TestBase
    {
        // TestData/manifest.json(.sig) için tek kullanımlık test anahtarı (bkz. TestData/README.md)
        internal const string TestPublicKey = "9enOkSVQHBXaXoAurkfvUFBqSbfYbJQbMwL0zEIPTqQ=";

        private static byte[] Manifest => File.ReadAllBytes(TestEnvironment.TestData("manifest.json"));
        private static string Signature => File.ReadAllText(TestEnvironment.TestData("manifest.json.sig"));

        [Fact]
        public void SignatureFromReleaseTooling_Verifies() =>
            Assert.True(ReleaseVerifier.VerifySignature(Manifest, Signature, TestPublicKey));

        [Fact]
        public void EmbeddedReleaseKey_RejectsSignatureMadeWithAnotherKey() =>
            Assert.False(ReleaseVerifier.VerifySignature(Manifest, Signature));

        [Fact]
        public void TamperedManifest_IsRejected()
        {
            byte[] manifest = Manifest;
            manifest[manifest.Length / 2] ^= 0x01;
            Assert.False(ReleaseVerifier.VerifySignature(manifest, Signature, TestPublicKey));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-base64!")]
        [InlineData("AAAAAAAAAAAAAAAA")]
        public void MalformedSignature_IsRejected(string signature) =>
            Assert.False(ReleaseVerifier.VerifySignature(Manifest, signature, TestPublicKey));

        [Fact]
        public void GeneratedKey_RoundTripAndSingleBitChange()
        {
            var generator = new Ed25519KeyPairGenerator();
            generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = generator.GenerateKeyPair();
            byte[] message = Encoding.UTF8.GetBytes("{\"schema\":\"pops-manifest/1\",\"version\":\"9.9.9\"}\n");
            var signer = new Ed25519Signer();
            signer.Init(true, pair.Private);
            signer.BlockUpdate(message, 0, message.Length);
            string signature = Convert.ToBase64String(signer.GenerateSignature());
            string publicKey = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());

            Assert.True(ReleaseVerifier.VerifySignature(message, signature, publicKey));
            message[5] ^= 0x01;
            Assert.False(ReleaseVerifier.VerifySignature(message, signature, publicKey));
        }

        [Fact]
        public void EmbeddedKey_MatchesRepositoryPublicKey()
        {
            string pem = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot(), "keys", "pops_release_ed25519.pub.pem"));
            byte[] der = Convert.FromBase64String(string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----")).Select(l => l.Trim())));
            Assert.Equal(44, der.Length); // SPKI önek (12) + ham anahtar (32)
            Assert.Equal(ReleaseVerifier.PublicKeyBase64, Convert.ToBase64String(der, 12, 32));
        }

        [Fact]
        public void Parse_ReadsVersionTagAndArtifacts()
        {
            ReleaseVerifier.Manifest manifest = ReleaseVerifier.Parse(Manifest);
            Assert.Equal("0.1.3-alpha", manifest.Version);
            Assert.Equal("v0.1.3-alpha", manifest.Tag);
            Assert.Contains(manifest.Artifacts, a => a.Name == "POps-Agent-0.1.3-alpha-win-x64.msi" && a.Size == 300000 && a.Sha256.Length == 64);
        }

        [Fact]
        public void Parse_UnknownSchema_Throws() =>
            Assert.Throws<FormatException>(() => ReleaseVerifier.Parse(Encoding.UTF8.GetBytes("{\"schema\":\"other/1\",\"version\":\"1.0.0\",\"artifacts\":[]}")));

        private static readonly string[] Ordered =
        {
            "0.1.1", "0.1.2-alpha", "0.1.2-alpha.1", "0.1.2-alpha.beta", "0.1.2-beta", "0.1.2-beta.2", "0.1.2-beta.11",
            "0.1.2-rc.1", "0.1.2", "0.1.3-alpha", "0.2.0", "1.0.0", "10.0.0",
        };

        public static IEnumerable<object[]> OrderedPairs() =>
            Ordered.Zip(Ordered.Skip(1), (lower, higher) => new object[] { lower, higher });

        [Theory]
        [MemberData(nameof(OrderedPairs))]
        public void CompareVersions_FollowsSemVerPrecedence(string lower, string higher)
        {
            Assert.True(ReleaseVerifier.CompareVersions(lower, higher) < 0);
            Assert.True(ReleaseVerifier.CompareVersions(higher, lower) > 0);
        }

        [Theory]
        [InlineData("v0.1.3-alpha", "0.1.3-alpha")]
        [InlineData("V1.0.0", "1.0.0+build.7")]
        [InlineData("0.1.2", "v0.1.2")]
        public void CompareVersions_IgnoresVPrefixAndBuildMetadata(string a, string b) =>
            Assert.Equal(0, ReleaseVerifier.CompareVersions(a, b));

        [Theory]
        [InlineData("")]
        [InlineData("1.2")]
        [InlineData("1.2.3.4")]
        [InlineData("a.b.c")]
        [InlineData("1.2.3-")]
        [InlineData("1.2.3-al..pha")]
        public void CompareVersions_MalformedVersion_Throws(string version) =>
            Assert.Throws<FormatException>(() => ReleaseVerifier.CompareVersions(version, "1.0.0"));

        // Ajan, manifest sürümü kurulu sürümden büyük değilse güncellemeyi reddeder (downgrade / aynı sürüm)
        [Theory]
        [InlineData("0.1.3-alpha", "0.1.3-alpha")]
        [InlineData("0.1.3-alpha", "0.1.3")]
        [InlineData("0.1.3-alpha", "v0.2.0")]
        public void OlderOrSameManifestVersion_IsNotAnUpgrade(string manifest, string installed) =>
            Assert.True(ReleaseVerifier.CompareVersions(manifest, installed) <= 0);
    }
}
