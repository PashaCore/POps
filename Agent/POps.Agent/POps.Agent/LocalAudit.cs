using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using POps.Shared;

#nullable disable

namespace POpsAgent
{
    public enum LocalAuditLevel { Information, Warning, Error }

    public sealed class LocalAuditEvent
    {
        public int EventId { get; init; }
        public LocalAuditLevel Level { get; init; }
        public string Message { get; init; }
    }

    // Yüksek etkili işlemlerin sunucudan bağımsız yerel denetim izi. Üretici işlevler saftır; yalnız Write
    // Windows Olay Günlüğüne dokunur ve başarısız olursa ajanı durdurmaz.
    public static class LocalAudit
    {
        public const string Source = "POps Agent";

        public static LocalAuditEvent CommandStarted(int taskId, string command, string requestedBy)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(command ?? "");
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return Info(1000, "Uzaktan komut başladı", ("task_id", taskId), ("command_sha256", hash),
                ("command_length", (command ?? "").Length), ("requested_by", Safe(requestedBy)));
        }

        public static LocalAuditEvent CommandFinished(int taskId, int exitCode, TimeSpan duration) =>
            Info(1001, "Uzaktan komut bitti", ("task_id", taskId), ("exit_code", exitCode),
                ("duration_ms", Math.Max(0, (long)duration.TotalMilliseconds)));

        public static LocalAuditEvent VisionStarted(string sessionId, string requestedBy, bool userApproved) =>
            Info(1010, "Vision oturumu başladı", ("session_id", Safe(sessionId)),
                ("requested_by", Safe(requestedBy)), ("user_approved", userApproved));

        public static LocalAuditEvent VisionFinished(string sessionId, string requestedBy, bool userApproved) =>
            Info(1011, "Vision oturumu bitti", ("session_id", Safe(sessionId)),
                ("requested_by", Safe(requestedBy)), ("user_approved", userApproved));

        public static LocalAuditEvent QuarantineStarted(string source) =>
            Info(1020, "Karantina başladı", ("source", Safe(source)));

        public static LocalAuditEvent QuarantineFinished(string source) =>
            Info(1021, "Karantina bitti", ("source", Safe(source)));

        public static LocalAuditEvent QuarantineAllowListRefreshed(IEnumerable<string> before, IEnumerable<string> after, string reason) =>
            Info(1022, "Karantina izin listesi yenilendi (sunucu adresi değişti)",
                ("old", before == null ? "(bilinmiyor)" : string.Join(", ", before)), ("new", string.Join(", ", after ?? Array.Empty<string>())),
                ("reason", Safe(reason)));

        public static LocalAuditEvent UpdateResult(string from, string to, string outcome, string rollback) =>
            Info(1030, "Güncelleme sonucu", ("from", Safe(from)), ("to", Safe(to)),
                ("outcome", Safe(outcome)), ("rollback", Safe(rollback)));

        public static LocalAuditEvent CapabilityChanged(string capability, bool before, bool after) =>
            Info(1040, "Yetenek değişti", ("capability", Safe(capability)), ("old", before), ("new", after));

        public static LocalAuditEvent AuthenticationRejected(string channel) =>
            Warning(1050, "Sunucu kimliği reddetti (4401)", ("channel", Safe(channel)));

        public static LocalAuditEvent BypassSecretReceived(string fingerprint) =>
            Info(1060, "Bypass anahtarı alındı", ("fingerprint", Safe(fingerprint)));

        // Açılışta donanım bağı (hw.bind) uyuşmadı: cihaza özel dosyalar klon klasörüne taşındı (bkz. HardwareBinding)
        public static LocalAuditEvent CloneDetected(string previousHwId, string newHwId, string folder, IEnumerable<string> moved, bool enrollToken) =>
            Warning(1070, "Kopyalanmış kurulum: cihaz anahtarı bu donanıma ait değil", ("old_hw_id", Safe(previousHwId)),
                ("new_hw_id", Safe(newHwId)), ("moved_to", Safe(folder)), ("files", Safe(string.Join(", ", moved ?? Array.Empty<string>()))),
                ("enroll_token", enrollToken ? "var" : "yok"));

        // Karşılaştırılabilen donanım değerlerinden biri değişti, biri aynı: kopya sayılmadı, dosyalara dokunulmadı
        public static LocalAuditEvent HardwarePartlyChanged(IEnumerable<string> changed, IEnumerable<string> same) =>
            Warning(1072, "Donanımın bir kısmı değişti; kopya kararı verilmedi", ("changed", Safe(string.Join(", ", changed ?? Array.Empty<string>()))),
                ("unchanged", Safe(string.Join(", ", same ?? Array.Empty<string>()))), ("action", "dosyalara dokunulmadı"));

        // Sunucunun laboratuvar modülleri değişti (politika yanıtı; bkz. AgentModules)
        public static LocalAuditEvent ModulesChanged(IEnumerable<string> closed, IEnumerable<string> opened) =>
            Info(1080, "Sunucu modülleri değişti", ("closed", Safe(Join(closed))), ("opened", Safe(Join(opened))));

        private static string Join(IEnumerable<string> values)
        {
            string text = string.Join(", ", values ?? Array.Empty<string>());
            return text.Length == 0 ? "-" : text;
        }

        public static LocalAuditEvent CloneRejected(string channel) =>
            Warning(1071, "Sunucu bu kimliği başka bir bilgisayarda bağlı buldu (4409)", ("channel", Safe(channel)));

        // Ajanın yapılandırması (appsettings.json, POPS_SERVER_URL) okunamadı: sunucu adresi son çaredir
        public static LocalAuditEvent ConfigUnreadable(string problem, string serverUrl) =>
            Build(1090, LocalAuditLevel.Error, "Yapılandırma okunamadı", ("problem", Safe(problem)), ("server_url", Safe(serverUrl)));

        public static void Write(LocalAuditEvent item)
        {
            if (item == null) return;
            try
            {
                EventLog.WriteEntry(Source, item.Message,
                    item.Level == LocalAuditLevel.Error ? EventLogEntryType.Error
                    : item.Level == LocalAuditLevel.Warning ? EventLogEntryType.Warning : EventLogEntryType.Information,
                    item.EventId);
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AUDIT", $"Windows Olay Günlüğüne {item.EventId} yazılamadı: {ex.Message}", true);
            }
        }

        private static LocalAuditEvent Info(int id, string title, params (string Key, object Value)[] fields) =>
            Build(id, LocalAuditLevel.Information, title, fields);

        private static LocalAuditEvent Warning(int id, string title, params (string Key, object Value)[] fields) =>
            Build(id, LocalAuditLevel.Warning, title, fields);

        private static LocalAuditEvent Build(int id, LocalAuditLevel level, string title, params (string Key, object Value)[] fields)
        {
            var text = new StringBuilder(title);
            foreach (var field in fields)
                text.Append('\n').Append(field.Key).Append(": ").Append(Convert.ToString(field.Value, CultureInfo.InvariantCulture));
            return new LocalAuditEvent { EventId = id, Level = level, Message = text.ToString() };
        }

        private static string Safe(string value) => LogText.Safe(value, 200);
    }
}
