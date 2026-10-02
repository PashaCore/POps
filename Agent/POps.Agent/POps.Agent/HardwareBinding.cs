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
    // Açılış denetiminin sonucu. Inconclusive: özet farklı ama değişen bir değer güvenilir okunamadı; dosyalara dokunulmaz.
    public enum BindingVerdict { Missing, Match, Clone, Inconclusive }

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
    // Her açılışta, kimlik ve secret okunmadan önce özet yeniden hesaplanır. Bağlama anında da şimdi de güvenilir okunan
    // bir değer (UUID ya da BIOS seri numarası) değiştiyse kurulum kopyalanmıştır: identity.key, secret, bypass.device ve
    // hw.bind silinmez, secure\clone-<zaman>\ altına taşınır. Ajan kimliği donanımdan yeniden türetir ve kayıtsız cihaz
    // olarak devam eder (enroll.token varsa onunla kaydolur). Değer okunamıyorsa (boş, sıfır UUID, "To be filled by O.E.M."
    // gibi) bir şeye dokunulmaz: geçici bir WMI hatası anahtarı kaybettirmesin.
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
        private bool _unreadableLogged;

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

        public static string PrimaryPath => SecureStore.PathOf(FileName);
        private static string MirrorPath() => AgentCredentials.PersistPath(FileName);

        // dna_payload ile aynı normalleştirme (Worker.GetHardwareDnaInternal)
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

        // Klon: bağlama anında da şimdi de güvenilir okunan bir parça değişmiş. Özet farklı ama böyle bir parça yoksa
        // (biri şimdi okunamıyor ya da yalnızca güvenilmez parça değişmiş) karar verilmez.
        public static BindingVerdict Evaluate(BindingRecord saved, string uuid, string biosSerial)
        {
            if (saved == null) return BindingVerdict.Missing;
            if (Same(saved.Digest, Digest(uuid, biosSerial))) return BindingVerdict.Match;
            bool changed = (saved.Uuid != null && IsReadableUuid(uuid) && !Same(saved.Uuid, Sha256(uuid)))
                || (saved.BiosSerial != null && IsReadableSerial(biosSerial) && !Same(saved.BiosSerial, Sha256(biosSerial)));
            return changed ? BindingVerdict.Clone : BindingVerdict.Inconclusive;
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
            BindingRecord saved = Load();
            BoundHwId = saved?.HwId;
            if (saved == null) return Verdict = BindingVerdict.Missing;

            var (uuid, bios) = Reading();
            Verdict = Evaluate(saved, uuid, bios);
            if (Verdict == BindingVerdict.Clone) SetAside();
            else if (Verdict == BindingVerdict.Inconclusive)
                LogUnreadableOnce($"Donanım özeti kayıtlı bağdan (hw.bind) farklı ama değişen değer güvenilir okunamadı "
                    + $"(UUID {Describe(IsReadableUuid(uuid))}, BIOS seri no {Describe(IsReadableSerial(bios))}); kopya denetimi yapılmadı, dosyalara dokunulmadı.");
            return Verdict;
        }

        // set_secret ve ilk kullanımda güven: bugünkü donanım yazılır. Hiçbir değer güvenilir okunamıyorsa yazılmaz
        // (sonraki açılışta yeniden denenir).
        public bool Bind(string hwId, string reason)
        {
            var (uuid, bios) = Reading();
            if (!IsReadableUuid(uuid) && !IsReadableSerial(bios))
            {
                LogUnreadableOnce($"Donanım bağı (hw.bind) yazılmadı ({reason}): UUID ve BIOS seri numarası güvenilir okunamadı.");
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
        }

        private string ReadIdentity()
        {
            try { return File.Exists(IdentityPath) ? File.ReadAllText(IdentityPath).Trim() : null; }
            catch { return null; }
        }

        private void LogUnreadableOnce(string message)
        {
            if (_unreadableLogged) return;
            _unreadableLogged = true;
            POpsHelpers.Log("SECURE", message, true);
        }

        private static string Describe(bool readable) => readable ? "okundu" : "okunamadı";
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
    }
}
