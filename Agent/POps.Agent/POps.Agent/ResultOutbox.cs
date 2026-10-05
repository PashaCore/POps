using System;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Görev sonuçlarının gönderimi (execute, retler, winget, güç işlemi, kullanıcı mesajı). Onaysız (eski) sunucuda bağlantı
    // yokken gönderilemeyen sonuçlar bellekte bekler (en çok MaxPendingResults); onaylı sunucuda diskte (ResultSpool).
    [SupportedOSPlatform("windows")]
    internal sealed class ResultOutbox
    {
        private const int MaxPendingResults = 20;

        private readonly Func<object, Task<bool>> _send;
        private readonly ConcurrentQueue<(int TaskId, object Result)> _pendingResults = new ConcurrentQueue<(int TaskId, object Result)>();

        // send: komut kanalına gönderim (dönen: o anki komut soketine yazıldı mı)
        public ResultOutbox(ServerHandshake handshake, ResultSpool results, Func<object, Task<bool>> send)
        {
            Handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
            Results = results ?? throw new ArgumentNullException(nameof(results));
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        // Sunucunun duyurduğu özellikler (result_ack) ve onay bekleyen sonuçlar (Worker'ınkilerle aynı örnekler)
        private ServerHandshake Handshake { get; }
        private ResultSpool Results { get; }

        private Task<bool> TrySendCommandMessageAsync(object payload) => _send(payload);

        // Görev sonucu. Onaylı sunucuda (result_ack; bağlantının ilk saniyelerinde, henüz bilinmiyorken önceki bağlantının
        // bildiği) önce diske yazılır, gönderilir ve onay gelene kadar kalır. Onaysız sunucuda eski davranış: gönderilemezse
        // bağlantı yeniden kurulunca gönderilmek üzere bellekte sırada bekler.
        public async Task SendResultAsync(int taskId, object result)
        {
            bool durable = Handshake.Supports(ResultSpool.AckFeature) ?? Handshake.LastKnown(ResultSpool.AckFeature) ?? false;
            if (durable)
            {
                Results.Add(taskId, result);
                if (Handshake.Supports(ResultSpool.AckFeature) == true && await TrySendCommandMessageAsync(result)) Results.MarkSent(taskId);
                return;
            }
            if (_pendingResults.IsEmpty && await TrySendCommandMessageAsync(result)) return;
            _pendingResults.Enqueue((taskId, result));
            while (_pendingResults.Count > MaxPendingResults && _pendingResults.TryDequeue(out _))
                POpsHelpers.Log("AGENT", "Gönderilemeyen görev sonuçları sınırı aşıldı; en eskisi atıldı.", true);
        }

        // Her heartbeat'te: bekleyen sonuçlar gönderilir. Onaylı sunucuda bellekteki kuyruk da diske geçer ve bu bağlantıda
        // henüz gönderilmemiş (yeniden bağlanınca: hepsi) onaysız sonuçlar gönderilir. Onaysız sunucuda diskte kalmışlar
        // (önceki sunucudan ya da önceki çalışmadan) gönderilince silinir: o sunucu onay göndermez.
        public async Task FlushPendingResultsAsync()
        {
            bool? ack = Handshake.Supports(ResultSpool.AckFeature);
            if (ack == true)
            {
                while (_pendingResults.TryDequeue(out var queued)) Results.Add(queued.TaskId, queued.Result);
                foreach (ResultSpool.Entry entry in Results.Unsent())
                {
                    if (!await TrySendCommandMessageAsync(entry.Result)) return;
                    Results.MarkSent(entry.TaskId);
                }
                return;
            }
            while (_pendingResults.TryPeek(out var queued))
            {
                if (!await TrySendCommandMessageAsync(queued.Result)) return;
                _pendingResults.TryDequeue(out _);
            }
            if (ack == false)
                foreach (ResultSpool.Entry entry in Results.All())
                {
                    if (!await TrySendCommandMessageAsync(entry.Result)) return;
                    Results.Remove(entry.TaskId);
                }
        }

        // {"action":"result_ack","task_id":N}: sonuç sunucuda yazıldı, diskten silinir
        public void HandleResultAck(JsonElement root)
        {
            if (!root.TryGetProperty("task_id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int taskId)) return;
            if (Results.Remove(taskId)) POpsHelpers.Log("AGENT", $"Sunucu görev sonucunu onayladı (TaskID: {taskId}).");
        }
    }
}
