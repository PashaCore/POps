using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

#nullable disable

namespace POpsAgent
{
    // İmzalı release manifest'inin doğrulanması (Faz 2'nin ajan yarısı). Manifest'i tools/sign_release.py
    // üretir; imza, manifest.json'un tam baytları üzerindeki ham ed25519 imzasıdır (base64). .NET'te yerleşik
    // ed25519 olmadığı için BouncyCastle kullanılır.
    public static class ReleaseVerifier
    {
        // keys/pops_release_ed25519.pub.pem'in ham 32 baytı (bkz. keys/README.md). Anahtar değişirse
        // (rotasyon) burası da değişmeli; eski anahtarla imzalı paketler o andan sonra reddedilir.
        public const string PublicKeyBase64 = "MQqVu9JHcHvpj0gI8FcrrtrqrCxSe4iAqKR2L/bZYuQ=";
        public const string ManifestSchema = "pops-manifest/1";

        public sealed class Artifact
        {
            public string Name { get; init; }
            public string Sha256 { get; init; }
            public long Size { get; init; }
        }

        public sealed class Manifest
        {
            public string Version { get; init; }
            public string Tag { get; init; }
            public long ReleasedAt { get; init; }
            public IReadOnlyList<Artifact> Artifacts { get; init; }
        }

        public static bool VerifySignature(byte[] manifestBytes, string signatureBase64, string publicKeyBase64 = PublicKeyBase64)
        {
            try
            {
                byte[] signature = Convert.FromBase64String(signatureBase64?.Trim() ?? "");
                byte[] key = Convert.FromBase64String(publicKeyBase64);
                if (manifestBytes == null || signature.Length != 64 || key.Length != 32) return false;

                var signer = new Ed25519Signer();
                signer.Init(false, new Ed25519PublicKeyParameters(key, 0));
                signer.BlockUpdate(manifestBytes, 0, manifestBytes.Length);
                return signer.VerifySignature(signature);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Yalnızca imzası doğrulanmış baytlar için çağrılır. Beklenen alanlar yoksa FormatException.
        public static Manifest Parse(byte[] manifestBytes)
        {
            using JsonDocument doc = JsonDocument.Parse(manifestBytes);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("manifest bir JSON nesnesi değil");
            if (Str(root, "schema") != ManifestSchema) throw new FormatException($"bilinmeyen manifest şeması: {Str(root, "schema")}");

            string version = Str(root, "version");
            if (ParseSemVer(version) == null) throw new FormatException($"manifest sürümü geçersiz: {version}");
            if (!root.TryGetProperty("artifacts", out JsonElement arts) || arts.ValueKind != JsonValueKind.Array)
                throw new FormatException("manifest'te artifacts yok");

            var artifacts = new List<Artifact>();
            foreach (JsonElement a in arts.EnumerateArray())
            {
                artifacts.Add(new Artifact
                {
                    Name = Str(a, "name"),
                    Sha256 = Str(a, "sha256")?.ToLowerInvariant(),
                    Size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long size) ? size : -1,
                });
            }

            return new Manifest
            {
                Version = version,
                Tag = Str(root, "tag"),
                ReleasedAt = root.TryGetProperty("released_at", out JsonElement r) && r.TryGetInt64(out long at) ? at : 0,
                Artifacts = artifacts,
            };
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ==========================================
        // SÜRÜM KARŞILAŞTIRMA (SemVer 2.0 önceliği)
        // ==========================================
        // "v" öneki ve "+derleme" eki yok sayılır. 0.1.2-alpha < 0.1.2 < 0.1.3-alpha.
        // Geçersiz sürümde FormatException.
        public static int CompareVersions(string a, string b)
        {
            var (coreA, preA) = ParseSemVer(a) ?? throw new FormatException($"geçersiz sürüm: {a}");
            var (coreB, preB) = ParseSemVer(b) ?? throw new FormatException($"geçersiz sürüm: {b}");

            for (int i = 0; i < 3; i++)
            {
                int c = coreA[i].CompareTo(coreB[i]);
                if (c != 0) return c;
            }
            // Ön sürüm etiketi olmayan, olandan büyüktür
            if (preA.Length == 0 || preB.Length == 0) return preB.Length.CompareTo(preA.Length);

            for (int i = 0; i < Math.Min(preA.Length, preB.Length); i++)
            {
                bool numA = IsNumeric(preA[i]), numB = IsNumeric(preB[i]);
                int c = numA && numB ? ulong.Parse(preA[i]).CompareTo(ulong.Parse(preB[i]))
                      : numA ? -1
                      : numB ? 1
                      : string.CompareOrdinal(preA[i], preB[i]);
                if (c != 0) return Math.Sign(c);
            }
            return preA.Length.CompareTo(preB.Length);
        }

        private static bool IsNumeric(string s) => s.Length > 0 && s.Length <= 18 && s.All(char.IsAsciiDigit);

        private static (ulong[] Core, string[] Pre)? ParseSemVer(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            string v = version.Trim();
            if (v.StartsWith('v') || v.StartsWith('V')) v = v.Substring(1);
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);

            int dash = v.IndexOf('-');
            string core = dash >= 0 ? v.Substring(0, dash) : v;
            string[] pre = dash >= 0 ? v.Substring(dash + 1).Split('.') : Array.Empty<string>();

            string[] parts = core.Split('.');
            if (parts.Length != 3 || !parts.All(IsNumeric)) return null;
            if (pre.Any(p => p.Length == 0 || !p.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))) return null;
            return (parts.Select(ulong.Parse).ToArray(), pre);
        }
    }
}
