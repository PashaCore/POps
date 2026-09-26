using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // İmzalı MSI ile ajan güncellemesi, ajan tarafı (Faz 7 + Faz 2 doğrulaması).
    //  1. Sunucu "update_agent" emrinde imzalı manifest'i gönderir: "manifest" = manifest.json'un baytları
    //     (base64), "manifest_sig" = manifest.json.sig içeriği.
    //  2. İmza gömülü ed25519 anahtarla doğrulanır; sürüm kurulu sürümden büyük olmalıdır (downgrade yok).
    //  3. MSI'ın adı, boyutu ve SHA-256'sı yalnızca imzalı manifest'ten alınır. Dosya yalnızca bu ajanın
    //     bağlı olduğu sunucunun /updates dizininden indirilir; boyut ya da özet tutmazsa uygulanmaz.
    //  4. Doğrulanan MSI C:\POpsData\updates'e konur (öğrenciler yazamaz) ve POpsUpdater, kurulum klasörü
    //     dışına kopyalanıp başlatılır: msiexec kurulum klasörünü değiştirirken kendi dosyasını kilitlemesin.
    // Doğrulamaların hepsi geçmeden hiçbir süreç durdurulmaz, hiçbir dosya değiştirilmez.
    // update.lock / health.json / update-result.json biçimi POpsUpdater ve POpsWatchdog ile ortaktır.
    [SupportedOSPlatform("windows")]
    public static class AgentUpdate
    {
        public static string DataDir { get; set; } = @"C:\POpsData";
        public static string UpdatesDir => Path.Combine(DataDir, "updates");
        public static string UpdaterDir => Path.Combine(DataDir, "updater");
        public static string LockPath => Path.Combine(DataDir, "update.lock");
        public static string HealthPath => Path.Combine(DataDir, "health.json");
        public static string ResultPath => Path.Combine(DataDir, "update-result.json");

        private static readonly Regex MsiNameRegex = new Regex(@"^POps-Agent-[A-Za-z0-9._-]+-win-x64\.msi$", RegexOptions.Compiled);
        private static readonly Regex Sha256HexRegex = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);
        private const long MaxPackageBytes = 512L * 1024 * 1024;
        private static readonly TimeSpan StaleLockAge = TimeSpan.FromMinutes(15);
        private static int _busy;

        // Ajanın kendi sürümü, manifest ve VERSION dosyasındaki biçimde ("0.1.2-alpha")
        public static string InstalledVersion => POpsHelpers.AppVersion.TrimStart('v');

        // ==========================================
        // health.json: updater yeni sürümün ayağa kalktığını buradan anlar
        // ==========================================
        public static void WriteHealth()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var health = new { version = InstalledVersion, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), pid = Environment.ProcessId };
                WriteAtomic(HealthPath, JsonSerializer.Serialize(health));
            }
            catch (Exception ex) { POpsHelpers.Log("UPDATE", $"health.json yazılamadı: {ex.Message}", true); }
        }

        public static void LogLastResult()
        {
            try
            {
                if (File.Exists(ResultPath)) POpsHelpers.Log("UPDATE", $"Son güncelleme sonucu: {File.ReadAllText(ResultPath).Trim()}");
            }
            catch { }
        }

        // ==========================================
        // update_agent emri
        // ==========================================
        public static async Task HandleUpdateCommandAsync(JsonElement command, HttpClient http, string serverUrl)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1)
            {
                POpsHelpers.Log("UPDATE", "Güncelleme zaten hazırlanıyor; yeni emir yok sayıldı.");
                return;
            }
            string packagePath = null;
            bool launched = false;
            try
            {
                if (IsLockFresh())
                {
                    POpsHelpers.Log("UPDATE", "Başka bir güncelleme sürüyor (update.lock); emir yok sayıldı.");
                    return;
                }

                string manifestBase64 = Str(command, "manifest");
                string signature = Str(command, "manifest_sig");
                if (manifestBase64 == null || signature == null)
                {
                    Reject("emirde imzalı manifest yok; imzasız paket uygulanmaz");
                    return;
                }
                byte[] manifestBytes;
                try { manifestBytes = Convert.FromBase64String(manifestBase64); }
                catch (FormatException) { Reject("manifest base64 değil"); return; }

                if (!ReleaseVerifier.VerifySignature(manifestBytes, signature))
                {
                    Reject("manifest imzası geçersiz (kurcalanmış ya da başka anahtarla imzalanmış)");
                    return;
                }
                ReleaseVerifier.Manifest manifest = ReleaseVerifier.Parse(manifestBytes);

                if (ReleaseVerifier.CompareVersions(manifest.Version, InstalledVersion) <= 0)
                {
                    Reject($"manifest sürümü ({manifest.Version}) kurulu sürümden ({InstalledVersion}) yeni değil");
                    return;
                }

                var msis = manifest.Artifacts.Where(a => a.Name != null && MsiNameRegex.IsMatch(a.Name)).ToList();
                if (msis.Count != 1)
                {
                    Reject($"manifest'te tek bir ajan MSI'ı bekleniyordu, {msis.Count} bulundu");
                    return;
                }
                ReleaseVerifier.Artifact msi = msis[0];
                if (msi.Sha256 == null || !Sha256HexRegex.IsMatch(msi.Sha256) || msi.Size <= 0 || msi.Size > MaxPackageBytes)
                {
                    Reject($"{msi.Name}: manifest'teki özet ya da boyut geçersiz");
                    return;
                }
                POpsHelpers.Log("UPDATE", $"[GÜVENLİK] İmzalı manifest doğrulandı: {InstalledVersion} -> {manifest.Version} ({msi.Name}, {manifest.Tag}).");

                Directory.CreateDirectory(UpdatesDir);
                foreach (string old in Directory.GetFiles(UpdatesDir)) TryDelete(old);
                packagePath = Path.Combine(UpdatesDir, msi.Name);
                string url = $"{serverUrl.TrimEnd('/')}/updates/{Uri.EscapeDataString(msi.Name)}";
                if (!await DownloadVerifiedAsync(http, url, packagePath, msi)) return;

                launched = LaunchUpdater(packagePath, msi.Sha256, manifest.Version);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("UPDATE", $"Güncelleme hazırlanamadı: {ex.Message}", true);
            }
            finally
            {
                if (!launched && packagePath != null) TryDelete(packagePath);
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static async Task<bool> DownloadVerifiedAsync(HttpClient http, string url, string path, ReleaseVerifier.Artifact expected)
        {
            POpsHelpers.Log("UPDATE", $"İndiriliyor: {url}");
            string partial = path + ".partial";
            TryDelete(partial);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Reject($"paket indirilemedi: HTTP {(int)response.StatusCode}");
                return false;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (Stream input = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cts.Token)) > 0)
                {
                    total += read;
                    if (total > expected.Size) break; // manifest'ten büyük paket okunmaya devam edilmez
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                }
            }

            byte[] actual = hash.GetHashAndReset();
            if (total != expected.Size || !CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected.Sha256)))
            {
                TryDelete(partial);
                Reject($"indirilen paket manifest'le uyuşmuyor (boyut {total}/{expected.Size}, SHA-256 {Convert.ToHexString(actual).ToLowerInvariant()}, beklenen {expected.Sha256})");
                return false;
            }
            File.Move(partial, path, true);
            POpsHelpers.Log("UPDATE", "[GÜVENLİK] Paket boyutu ve SHA-256'sı imzalı manifest'le eşleşti.");
            return true;
        }

        private static bool LaunchUpdater(string msiPath, string sha256, string toVersion)
        {
            string installDir = AppContext.BaseDirectory.TrimEnd('\\');
            string[] files;
            try { files = UpdaterFiles(installDir).ToArray(); }
            catch (Exception ex) when (ex is FileNotFoundException || ex is JsonException || ex is InvalidDataException)
            {
                Reject($"POpsUpdater kopyalanamıyor: {ex.Message}");
                return false;
            }

            if (Directory.Exists(UpdaterDir)) Directory.Delete(UpdaterDir, true);
            Directory.CreateDirectory(UpdaterDir);
            foreach (string f in files) File.Copy(f, Path.Combine(UpdaterDir, Path.GetFileName(f)), true);

            WriteAtomic(LockPath, JsonSerializer.Serialize(new { from_version = InstalledVersion, to_version = toVersion, started_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(UpdaterDir, "POpsUpdater.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = UpdaterDir,
                };
                foreach (string arg in new[] { "--msi", msiPath, "--sha256", sha256, "--from", InstalledVersion, "--to", toVersion, "--installdir", installDir })
                    psi.ArgumentList.Add(arg);

                using Process updater = Process.Start(psi) ?? throw new InvalidOperationException("süreç başlatılamadı");
                POpsHelpers.Log("UPDATE", $"POpsUpdater başlatıldı (PID {updater.Id}); servis kurulum sırasında durup yeni sürümle açılacak.");
                return true;
            }
            catch
            {
                TryDelete(LockPath);
                throw;
            }
        }

        // Updater'ın çalışması için gereken dosyaların tamamı: kendi dosyaları + POpsUpdater.deps.json'daki her
        // kütüphanenin çalışma zamanı dosyaları (dotnet publish -r win-x64 hepsini kurulum klasörünün köküne koyar).
        // Paylaşılan .NET çalışma zamanındaki derlemeler (ör. Microsoft.Win32.Registry) deps.json'da yer almaz.
        // Listelenen bir dosya eksikse updater hiç başlatılmaz: güncellemenin ortasında düşerdi.
        public static IEnumerable<string> UpdaterFiles(string installDir)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "POpsUpdater.exe", "POpsUpdater.dll", "POpsUpdater.deps.json", "POpsUpdater.runtimeconfig.json",
            };
            string depsPath = Path.Combine(installDir, "POpsUpdater.deps.json");
            if (!File.Exists(depsPath)) throw new FileNotFoundException("POpsUpdater.deps.json yok", depsPath);

            using (JsonDocument deps = JsonDocument.Parse(File.ReadAllText(depsPath)))
            {
                if (!deps.RootElement.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("POpsUpdater.deps.json'da targets yok");
                foreach (JsonProperty target in targets.EnumerateObject())
                    foreach (JsonProperty library in target.Value.EnumerateObject())
                        foreach (string section in new[] { "runtime", "runtimeTargets", "native" })
                            if (library.Value.TryGetProperty(section, out JsonElement assets) && assets.ValueKind == JsonValueKind.Object)
                                foreach (JsonProperty asset in assets.EnumerateObject())
                                    names.Add(Path.GetFileName(asset.Name));
            }

            foreach (string name in names)
            {
                string path = Path.Combine(installDir, name);
                if (!File.Exists(path)) throw new FileNotFoundException($"{name} kurulum klasöründe yok", path);
                yield return path;
            }
        }

        public static bool IsLockFresh()
        {
            try
            {
                var lockFile = new FileInfo(LockPath);
                return lockFile.Exists && DateTime.UtcNow - lockFile.LastWriteTimeUtc < StaleLockAge;
            }
            catch { return false; }
        }

        private static void Reject(string reason) => POpsHelpers.Log("UPDATE", $"[GÜVENLİK] Güncelleme reddedildi: {reason}.", true);

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, true);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
