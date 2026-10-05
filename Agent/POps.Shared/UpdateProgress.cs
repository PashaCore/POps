#nullable disable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POps.Shared
{
    // Updater -> servis: kurulumun aşaması (C:\POpsData\update-progress.json). Updater ana kurulumda msiexec'i
    // başlatmadan önce "installing", Windows Installer meşgulken (1618) "waiting_installer" yazar; bitince
    // update-result.json'dan ÖNCE siler. Servis heartbeat döngüsünde okur ve sunucuya "update_progress" olarak iletir.
    // run: update.lock'taki started_at; eski bir çalışmadan kalan dosya böylece ayırt edilir. Biçim yalnızca burada.
    public sealed class UpdateProgressRecord
    {
        [JsonPropertyName("schema")] public string Schema { get; set; } = UpdateProgressFile.Schema;
        [JsonPropertyName("run")] public long Run { get; set; }
        [JsonPropertyName("to_version")] public string ToVersion { get; set; }
        [JsonPropertyName("stage")] public string Stage { get; set; }
        [JsonPropertyName("attempt")] public int? Attempt { get; set; }
        [JsonPropertyName("of")] public int? Of { get; set; }
        [JsonPropertyName("detail")] public string Detail { get; set; }
        // Yazıldığı an (Unix saniye)
        [JsonPropertyName("at")] public long At { get; set; }
    }

    public static class UpdateProgressFile
    {
        public const string FileName = "update-progress.json";
        public const string Schema = "pops-update-progress/1";
        public const string Installing = "installing";
        public const string WaitingInstaller = "waiting_installer";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Updater'ın yazdığı aşamalar; servis yalnızca bunları dosyadan iletir
        public static bool IsUpdaterStage(string stage) => stage == Installing || stage == WaitingInstaller;

        public static string Serialize(UpdateProgressRecord record) => JsonSerializer.Serialize(record, Options);

        // Şeması farklı, aşaması updater'ın değil ya da çalışması belirsiz kayıt: null
        public static UpdateProgressRecord Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                UpdateProgressRecord record = JsonSerializer.Deserialize<UpdateProgressRecord>(json, Options);
                return record != null && record.Schema == Schema && IsUpdaterStage(record.Stage) && record.Run > 0 ? record : null;
            }
            catch (JsonException) { return null; }
        }

        // Geçici dosya + yerine taşıma: servis yarım yazılmış dosya okumaz
        public static void Write(string path, UpdateProgressRecord record)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(record));
            File.Move(tmp, path, true);
        }

        public static UpdateProgressRecord Read(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static void Delete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
