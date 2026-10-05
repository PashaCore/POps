using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using POps.Shared;

#nullable disable

namespace POpsAgent
{
    // Açılış denetiminin sonucu. Inconclusive: karşılaştırılabilen değerlerden biri aynı, biri farklı. Unreadable: özet
    // farklı ama karşılaştırılabilecek değer yok. İkisinde de dosyalara dokunulmaz.
    public enum BindingVerdict { Missing, Match, Clone, Inconclusive, Unreadable }

    // secure\hw.bind: cihaz anahtarının alındığı donanım
    public sealed class BindingRecord
    {
        // SHA-256("uuid|bios_sn"); değerler dna_payload'daki gibi normalleştirilmiş
        [JsonPropertyName("digest")] public string Digest { get; set; }
        // Parçaların ayrı özetleri; bağlama anında güvenilir okunamayan parça null
        [JsonPropertyName("uuid")] public string Uuid { get; set; }
        [JsonPropertyName("bios_sn")] public string BiosSerial { get; set; }
        // Anahtarın verildiği kimlik
        [JsonPropertyName("hw_id")] public string HwId { get; set; }
        [JsonPropertyName("saved_at")] public long SavedAt { get; set; }
    }

    // Cihaz anahtarı (secret) alındığı donanımda kalır. Anahtar gelince (set_secret) ya da anahtarı olup bağı olmayan
    // eski kurulumun ilk açılışında (ilk kullanımda güven) bugünkü donanımın özeti secure\hw.bind'e yazılır. PersistDir
    // ayarlıysa oraya da yazılır: dondurma yazılımı C:'yi geri alsa da bağ secret'la birlikte kalır, en yeni kayıt geçerlidir.
    // Her açılışta, kimlik ve secret okunmadan önce özet yeniden hesaplanır. Karşılaştırılabilen değerlerin (bağlama anında
    // da şimdi de güvenilir okunan UUID ve BIOS seri numarası) hepsi değiştiyse kurulum kopyalanmıştır: identity.key,
    // secret, bypass.device, hw.bind ve onay bekleyen görev sonuçları silinmez, secure\clone-<zaman>\ altına taşınır. Ajan
    // kimliği donanımdan yeniden türetir ve kayıtsız cihaz olarak devam eder (enroll.token varsa onunla kaydolur).
    // Kopya imaj başka makinede ikisini birden değiştirir; yalnızca biri değiştiyse (anakart servisi, BIOS seri numarası
    // düzeltmesi, sanal makine ayarı) karar verilmez, sunucunun 4409'u arkadan korur. Değer okunamıyorsa (boş, sıfır
    // UUID, "To be filled by O.E.M." gibi) bir şeye dokunulmaz: geçici bir WMI hatası anahtarı kaybettirmesin.
    [SupportedOSPlatform("windows")]
    public sealed class HardwareBinding
    {
        public const string FileName = "hw.bind";
        public const string CloneFolderPrefix = "clone-";
        // PersistDir kopyalarının klon klasöründeki adı (birincil kopyayla çakışmasın)
        public const string PersistSuffix = ".persist";

        // Bazı anakartların hepsinde aynı olan UUID (AMI varsayılanı): cihazı ayırt etmez
        private static readonly string[] SharedUuids = { "03000200-0400-0500-0006-000700080009" };
        private static readonly string[] PlaceholderSerials =
        {
            "Default string", "System Serial Number", "Chassis Serial Number", "Base Board Serial Number", "SerialNumber",
            "Not Specified", "Not Applicable", "Not Available", "None", "N/A", "NA", "Unknown", "Invalid",
            "123456789", "1234567890", "0123456789",
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions { WriteIndented = true };

        private readonly Func<(string Uuid, string BiosSerial)> _read;
        private readonly Func<DateTimeOffset> _now;
        private (string Uuid, string BiosSerial)? _reading;
        private readonly HashSet<string> _logged = new HashSet<string>();

        // read: ham WMI değerleri (Win32_ComputerSystemProduct.UUID, Win32_BIOS.SerialNumber; okunamazsa "-")
        public HardwareBinding(string identityPath, Func<(string Uuid, string BiosSerial)> read, Func<DateTimeOffset> now = null)
        {
            IdentityPath = identityPath;
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public string IdentityPath { get; }
        // Son açılış denetimi
        public BindingVerdict Verdict { get; private set; } = BindingVerdict.Missing;
        public string BoundHwId { get; private set; }
        // Klonda: dosyaların taşındığı klasör, taşınanlar ve eski kimlik
        public string CloneFolder { get; private set; }
        public IReadOnlyList<string> MovedFiles { get; private set; } = Array.Empty<string>();
        public string PreviousHwId { get; private set; }
        // Karşılaştırılan parçalar ("uuid", "bios_sn"): değişenler ve aynı kalanlar
        public IReadOnlyList<string> ChangedParts { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> SameParts { get; private set; } = Array.Empty<string>();

        public static string PrimaryPath => SecureStore.PathOf(FileName);
        private static string MirrorPath() => AgentCredentials.PersistPath(FileName);

        // dna_payload ile aynı normalleştirme (HardwareInfo.GetHardwareDnaInternal)
        public static string NormalizeUuid(string raw) =>
            raw == null || raw == "-" || raw == "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF" ? "NULL" : raw;

        public static string NormalizeBiosSerial(string raw) =>
            raw == null || raw == "-" || raw.Contains("O.E.M") ? "NULL" : raw;

        public static string Digest(string uuid, string biosSerial) => Sha256(uuid + "|" + biosSerial);

        // Cihazı ayırt eden gerçek bir değer mi: boş, NULL, tamamı 0 ya da F, ortak varsayılan UUID değilse
        public static bool IsReadableUuid(string uuid)
        {
            if (!HasValue(uuid)) return false;
            string hex = uuid.Replace("-", "");
            if (hex.All(c => c == '0') || hex.All(c => c == 'F' || c == 'f')) return false;
            return !SharedUuids.Contains(uuid.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsReadableSerial(string serial)
        {
            if (!HasValue(serial)) return false;
            string value = serial.Trim();
            // 00000000, XXXXXXXX, ........
            if (value.All(c => c == value[0])) return false;
            if (value.Contains("O.E.M", StringComparison.OrdinalIgnoreCase)) return false;
            return !PlaceholderSerials.Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        private static bool HasValue(string value) => !string.IsNullOrWhiteSpace(value) && value != "NULL" && value != "-";

        public static BindingRecord CreateRecord(string uuid, string biosSerial, string hwId, DateTimeOffset now) => new BindingRecord
        {
            Digest = Digest(uuid, biosSerial),
            Uuid = IsReadableUuid(uuid) ? Sha256(uuid) : null,
            BiosSerial = IsReadableSerial(biosSerial) ? Sha256(biosSerial) : null,
            HwId = hwId,
            SavedAt = now.ToUnixTimeMilliseconds(),
        };

        public static BindingVerdict Evaluate(BindingRecord saved, string uuid, string biosSerial) =>
            Compare(saved, uuid, biosSerial).Verdict;

        // Karşılaştırılabilen parça: bağlama anında da şimdi de güvenilir okunan. Hepsi değiştiyse klon (tek parça
        // karşılaştırılabiliyorsa o karar verir); biri aynı biri farklıysa karar verilmez. Hepsi aynıysa aynı makinedir:
        // özetteki fark yalnızca güvenilmez parçadan gelir (ör. BIOS güncellemesi boş seri numarasını doldurdu).
        public static (BindingVerdict Verdict, List<string> Changed, List<string> Same) Compare(BindingRecord saved, string uuid, string biosSerial)
        {
            var changed = new List<string>();
            var same = new List<string>();
            if (saved == null) return (BindingVerdict.Missing, changed, same);
            if (Same(saved.Digest, Digest(uuid, biosSerial))) return (BindingVerdict.Match, changed, same);
            Part("uuid", saved.Uuid, IsReadableUuid(uuid) ? uuid : null);
            Part("bios_sn", saved.BiosSerial, IsReadableSerial(biosSerial) ? biosSerial : null);
            BindingVerdict verdict = changed.Count + same.Count == 0 ? BindingVerdict.Unreadable
                : same.Count == 0 ? BindingVerdict.Clone
                : changed.Count == 0 ? BindingVerdict.Match
                : BindingVerdict.Inconclusive;
            return (verdict, changed, same);

            void Part(string name, string savedHash, string current)
            {
                if (savedHash == null || current == null) return;
                (Same(savedHash, Sha256(current)) ? same : changed).Add(name);
            }
        }

        public static BindingRecord Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var record = JsonSerializer.Deserialize<BindingRecord>(json);
                return record != null && !string.IsNullOrEmpty(record.Digest) ? record : null;
            }
            catch (JsonException) { return null; }
        }

        // Birincil ve PersistDir kopyasından en yenisi (eşitlikte birincil)
        public static BindingRecord Load()
        {
            BindingRecord primary = Parse(SecureStore.Read(PrimaryPath));
            string mirrorPath = MirrorPath();
            BindingRecord mirror = mirrorPath == null ? null : Parse(SecureStore.Read(mirrorPath, requireTrustedOwner: true));
            if (primary == null) return mirror;
            if (mirror == null) return primary;
            return mirror.SavedAt > primary.SavedAt ? mirror : primary;
        }

        // Okunan değerler normalleştirilmiş olarak saklanır; hiçbiri güvenilir değilse bir sonraki çağrıda yeniden okunur
        private (string Uuid, string BiosSerial) Reading()
        {
            if (_reading != null) return _reading.Value;
            var raw = _read();
            var reading = (NormalizeUuid(raw.Uuid), NormalizeBiosSerial(raw.BiosSerial));
            if (IsReadableUuid(reading.Item1) || IsReadableSerial(reading.Item2)) _reading = reading;
            return reading;
        }

        // Açılışta, kimlik ve secret okunmadan önce
        public BindingVerdict CheckOnStartup()
        {
            CloneFolder = null;
            MovedFiles = Array.Empty<string>();
            PreviousHwId = null;
            ChangedParts = SameParts = Array.Empty<string>();
            BindingRecord saved = Load();
            BoundHwId = saved?.HwId;
            if (saved == null) return Verdict = BindingVerdict.Missing;

            var (uuid, bios) = Reading();
            var result = Compare(saved, uuid, bios);
            Verdict = result.Verdict;
            ChangedParts = result.Changed;
            SameParts = result.Same;
            if (Verdict == BindingVerdict.Clone) SetAside();
            else if (Verdict == BindingVerdict.Unreadable)
                LogOnce("unreadable", $"Donanım özeti kayıtlı bağdan (hw.bind) farklı ama karşılaştırılabilecek değer güvenilir okunamadı "
                    + $"(UUID {Describe(IsReadableUuid(uuid))}, BIOS seri no {Describe(IsReadableSerial(bios))}); kopya denetimi yapılmadı, dosyalara dokunulmadı.");
            else if (Verdict == BindingVerdict.Inconclusive)
                LogOnce("partial", $"[GÜVENLİK] Donanımın bir kısmı kayıtlı bağdan (hw.bind) farklı (değişen: {string.Join(", ", ChangedParts)}; "
                    + $"aynı: {string.Join(", ", SameParts)}); kopya sayılmadı, dosyalara dokunulmadı.");
            else if (Verdict == BindingVerdict.Match && !Same(saved.Digest, Digest(uuid, bios)))
                LogOnce("unreliable", $"Donanım özeti kayıtlı bağdan farklı ama güvenilir okunan değerler ({string.Join(", ", SameParts)}) aynı; aynı bilgisayar sayıldı.");
            return Verdict;
        }

        // set_secret ve ilk kullanımda güven: bugünkü donanım yazılır. Hiçbir değer güvenilir okunamıyorsa yazılmaz
        // (sonraki açılışta yeniden denenir).
        public bool Bind(string hwId, string reason)
        {
            var (uuid, bios) = Reading();
            if (!IsReadableUuid(uuid) && !IsReadableSerial(bios))
            {
                LogOnce("bind", $"Donanım bağı (hw.bind) yazılmadı ({reason}): UUID ve BIOS seri numarası güvenilir okunamadı.");
                return false;
            }
            return Write(CreateRecord(uuid, bios, hwId, _now()), reason);
        }

        // set_identity: donanım aynı, yalnızca anahtarın kimliği değişir
        public void UpdateHwId(string hwId)
        {
            BindingRecord saved = Load();
            if (saved == null || saved.HwId == hwId) return;
            saved.HwId = hwId;
            saved.SavedAt = _now().ToUnixTimeMilliseconds();
            Write(saved, "set_identity");
        }

        private static bool Write(BindingRecord record, string reason)
        {
            string json = JsonSerializer.Serialize(record, WriteOptions);
            bool saved = TryWrite(PrimaryPath, json);
            string mirrorPath = MirrorPath();
            if (mirrorPath != null) saved |= TryWrite(mirrorPath, json);
            if (saved) POpsHelpers.Log("SECURE", $"Cihaz anahtarı bu donanıma bağlandı (hw.bind, {reason}).");
            return saved;
        }

        private static bool TryWrite(string path, string json)
        {
            try
            {
                SecureStore.WriteProtected(path, json);
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("SECURE", $"Donanım bağı yazılamadı ({path}): {ex.Message}", true);
                return false;
            }
        }

        // Cihaza özel dosyalar klon klasörüne taşınır (silinmez: yönetici neyin kopyalandığını görebilsin)
        private void SetAside()
        {
            PreviousHwId = ReadIdentity();
            var moved = new List<string>();
            try
            {
                SecureStore.EnsureDirectory();
                string baseName = SecureStore.PathOf(CloneFolderPrefix + _now().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                string folder = baseName;
                for (int i = 2; Directory.Exists(folder); i++) folder = baseName + "-" + i;
                Directory.CreateDirectory(folder);
                CloneFolder = folder;
                foreach (var (source, name) in DeviceFiles())
                {
                    if (source == null || !File.Exists(source)) continue;
                    try
                    {
                        File.Move(source, Path.Combine(folder, name));
                        moved.Add(name);
                    }
                    catch (Exception ex) { POpsHelpers.Log("SECURE", $"[GÜVENLİK] {source} klon klasörüne taşınamadı: {ex.Message}", true); }
                }
            }
            catch (Exception ex) { POpsHelpers.Log("SECURE", $"[GÜVENLİK] Klon klasörü oluşturulamadı: {ex.Message}", true); }
            MovedFiles = moved;
        }

        private IEnumerable<(string Source, string Name)> DeviceFiles()
        {
            yield return (IdentityPath, Path.GetFileName(IdentityPath));
            yield return (SecureStore.PathOf(AgentCredentials.SecretFileName), AgentCredentials.SecretFileName);
            yield return (AgentCredentials.PersistPath(AgentCredentials.SecretFileName), AgentCredentials.SecretFileName + PersistSuffix);
            yield return (SecureStore.PathOf(AgentCredentials.DeviceBypassSecretFileName), AgentCredentials.DeviceBypassSecretFileName);
            yield return (PrimaryPath, FileName);
            yield return (MirrorPath(), FileName + PersistSuffix);
            // Asıl cihazın görev sonuçları yeni kimlikle gönderilmesin
            yield return (SecureStore.PathOf(ResultSpool.FileName), ResultSpool.FileName);
        }

        private string ReadIdentity()
        {
            try { return File.Exists(IdentityPath) ? File.ReadAllText(IdentityPath).Trim() : null; }
            catch { return null; }
        }

        private void LogOnce(string key, string message)
        {
            if (_logged.Add(key)) POpsHelpers.Log("SECURE", message, true);
        }

        private static string Describe(bool readable) => readable ? "okundu" : "okunamadı";
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
    }
}
