using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Güncellemenin sunucuya bildirimi. update_result: POpsUpdater'ın bıraktığı sonuç, onaylı sunucuda onaya kadar saklanır
    // (bkz. UpdateResultReporter). update_progress: updater'ın yazdığı aşama ve AgentUpdate'in geri çağrısı (UpdateHandler);
    // yalnızca bu bağlantıda ilk heartbeat gittiyse. Heartbeat turu ikisine de bakar.
    [SupportedOSPlatform("windows")]
    internal sealed class UpdateReporter
    {
        private readonly ServerHandshake _handshake;
        private readonly UpdateResultReporter _updateResults;
        private readonly Func<object, Task<bool>> _send;
        private readonly Action<LocalAuditEvent> _audit;

        // Bu bağlantıda ilk heartbeat gitti mi (sunucu cihazı ilk mesajın dna_payload'ından kaydeder)
        private volatile bool _heartbeatSent;
        // Bu bağlantıda iletilen son updater aşaması (aşama|deneme|zaman)
        private string? _forwardedProgress;

        // handshake, updateResults: Worker'ınkilerle aynı örnekler; send: komut kanalına gönderim (dönen: gönderildi mi);
        // audit: yerel denetim izi (Olay Günlüğü)
        public UpdateReporter(ServerHandshake handshake, UpdateResultReporter updateResults, Func<object, Task<bool>> send,
            Action<LocalAuditEvent> audit)
        {
            _handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
            _updateResults = updateResults ?? throw new ArgumentNullException(nameof(updateResults));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }

        // Yeni komut bağlantısı: update_progress ilk mesaj heartbeat olana kadar gönderilmez; son aşama yeni bağlantıda bir
        // kez daha gider
        internal void OnCommandSocketOpened()
        {
            _heartbeatSent = false;
            _forwardedProgress = null;
        }

        internal void OnHeartbeatSent() => _heartbeatSent = true;

        // update_progress: yalnızca sunucu özelliği duyurduysa ve bu bağlantıda heartbeat gittiyse; en iyi çaba (soket
        // kapalıysa düşer, saklanmaz). Dönen: gönderildi mi.
        internal async Task<bool> ReportUpdateProgressAsync(Dictionary<string, object> message)
        {
            if (!_heartbeatSent || _handshake.Supports(AgentUpdate.ProgressFeature) != true) return false;
            return await _send(message);
        }

        // Heartbeat döngüsünde: updater'ın yazdığı aşama (installing, waiting_installer) değiştiyse sunucuya iletilir
        internal async Task ForwardUpdateProgressAsync()
        {
            UpdateProgressRecord? record = AgentUpdate.PendingProgress();
            if (record == null) return;
            string key = $"{record.Stage}|{record.Attempt}|{record.At}";
            if (key == _forwardedProgress) return;
            if (await ReportUpdateProgressAsync(AgentUpdate.ProgressMessage(record.Stage, record.ToVersion, record.Attempt, record.Of, record.Detail)))
                _forwardedProgress = key;
        }

        // POpsUpdater'ın bıraktığı sonuç (update-result.json) sunucuya "update_result" olarak iletilir. Updater sonucu
        // yeni sürüm açıldıktan sonra yazdığı için her heartbeat'te bakılır. Onaylı sunucuda dosya onaya kadar kalır.
        internal async Task ReportUpdateResultAsync(CancellationToken token)
        {
            Dictionary<string, object>? message = AgentUpdate.PendingResultMessage();
            if (message == null) return;
            // Yerel denetim izi (1030) sunucu bağlantısından bağımsız, sonuç ilk görüldüğünde
            _audit(AgentUpdate.PendingResultAudit(message));

            string? resultId = message["result_id"] as string;
            UpdateResultReporter.Step step = _updateResults.Next(resultId);
            if (step == UpdateResultReporter.Step.Nothing || step == UpdateResultReporter.Step.Wait) return;

            if (!await _send(message)) return;

            if (step == UpdateResultReporter.Step.SendAndMarkReported)
            {
                AgentUpdate.MarkResultReported();
                POpsHelpers.Log("UPDATE", $"Güncelleme sonucu sunucuya iletildi: {message["status"]}.");
            }
            else
            {
                _updateResults.Sent(resultId);
                POpsHelpers.Log("UPDATE", $"Güncelleme sonucu sunucuya iletildi, onay bekleniyor: {message["status"]} ({resultId}).");
            }
        }
    }
}
