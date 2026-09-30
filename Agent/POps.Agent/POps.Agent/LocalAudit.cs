using System;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using POps.Shared;

#nullable disable

namespace POpsAgent
{
    public enum LocalAuditLevel { Information, Warning }

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

        public static LocalAuditEvent UpdateResult(string from, string to, string outcome, string rollback) =>
            Info(1030, "Güncelleme sonucu", ("from", Safe(from)), ("to", Safe(to)),
                ("outcome", Safe(outcome)), ("rollback", Safe(rollback)));

        public static LocalAuditEvent CapabilityChanged(string capability, bool before, bool after) =>
            Info(1040, "Yetenek değişti", ("capability", Safe(capability)), ("old", before), ("new", after));

        public static LocalAuditEvent AuthenticationRejected(string channel) =>
            Warning(1050, "Sunucu kimliği reddetti (4401)", ("channel", Safe(channel)));

        public static LocalAuditEvent BypassSecretReceived(string fingerprint) =>
            Info(1060, "Bypass anahtarı alındı", ("fingerprint", Safe(fingerprint)));

        public static void Write(LocalAuditEvent item)
        {
            if (item == null) return;
            try
            {
                EventLog.WriteEntry(Source, item.Message,
                    item.Level == LocalAuditLevel.Warning ? EventLogEntryType.Warning : EventLogEntryType.Information,
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
