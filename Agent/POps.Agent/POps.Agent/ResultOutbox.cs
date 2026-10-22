using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Görev sonuçlarının gönderimi (execute, retler, winget, güç işlemi, kullanıcı mesajı). Onaysız (eski) sunucuda bağlantı
    // yokken gönderilemeyen sonuçlar bellekte bekler (en çok MaxPendingResults); onaylı sunucuda diskte (ResultSpool).
    // Bağlantıyı kesecek bir işlemin (kapatma, yeniden başlatma) sonucu işlemden önce Store ile diske yazılır ama
    // gönderilmez; işlemin gerçek sonucu belli olunca SendStoredAsync onu değiştirir ve gönderir.
    [SupportedOSPlatform("windows")]
    internal sealed class ResultOutbox
    {
        private const int MaxPendingResults = 20;

        private readonly Func<object, Task<bool>> _send;
        private readonly ConcurrentQueue<(int TaskId, object Result)> _pendingResults = new ConcurrentQueue<(int TaskId, object Result)>();
        // Store ile saklanıp henüz gönderilmeyecek sonuçlar: heartbeat'teki gönderim bunları atlar. Yalnızca bellekte: servis
        // yeniden başlarsa (bilgisayar kapandıysa) diskteki sonuç her zamanki gibi gönderilir.
        private readonly HashSet<int> _held = new HashSet<int>();

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

        // Göndermeden saklar: sonuç, sunucu türünden bağımsız olarak gönderilmemiş diye diske yazılır ve SendStoredAsync'e
        // kadar gönderilmez (heartbeat de göndermez). Bağlantıyı kesecek işlemden önce çağrılır: işlem bilgisayarı kapatırsa
        // sonuç yeniden açılışta diskten gider.
        public void Store(int taskId, object result)
        {
            lock (_held) _held.Add(taskId);
            Results.Add(taskId, result);
        }

        // Store ile saklanan sonuç result ile değiştirilir ve gönderilir. Onaylı sunucuda onaya kadar diskte kalır (bağlantının
        // ilk saniyelerinde, sunucu henüz bilinmiyorken gönderilmez; server_info'dan sonraki heartbeat gönderir). Onaysız
        // sunucuda gönderilince diskten silinir (onay gelmez); gönderilemezse diskte kalır ve bağlantı kurulunca gider.
        public async Task SendStoredAsync(int taskId, object result)
        {
            // Önce değiştirilir, sonra bırakılır: heartbeat eski sonucu hiçbir an gönderemez
            Results.Add(taskId, result);
            lock (_held) _held.Remove(taskId);
            bool? ack = Handshake.Supports(ResultSpool.AckFeature);
            if (ack ?? Handshake.LastKnown(ResultSpool.AckFeature) ?? false)
            {
                if (ack == true && await TrySendCommandMessageAsync(result)) Results.MarkSent(taskId);
                return;
            }
            if (await TrySendCommandMessageAsync(result)) Results.Remove(taskId);
        }

        private bool IsHeld(int taskId)
        {
            lock (_held) return _held.Contains(taskId);
        }

        // Her heartbeat'te: bekleyen sonuçlar gönderilir. Onaylı sunucuda bellekteki kuyruk da diske geçer ve bu bağlantıda
        // henüz gönderilmemiş (yeniden bağlanınca: hepsi) onaysız sonuçlar gönderilir. Onaysız sunucuda diskte kalmışlar
        // (önceki sunucudan ya da önceki çalışmadan) gönderilince silinir: o sunucu onay göndermez. Store ile tutulanlar
        // atlanır.
        public async Task FlushPendingResultsAsync()
        {
            bool? ack = Handshake.Supports(ResultSpool.AckFeature);
            if (ack == true)
            {
                while (_pendingResults.TryDequeue(out var queued)) Results.Add(queued.TaskId, queued.Result);
                foreach (ResultSpool.Entry entry in Results.Unsent())
                {
                    if (IsHeld(entry.TaskId)) continue;
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
                    if (IsHeld(entry.TaskId)) continue;
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

    // Güç işlemi ve kullanıcı mesajı sonuçları: execute'un result'ıyla aynı biçim ({type, pc_name, task_id, output,
    // exit_code}) ve aynı saklama/onay yolu (bkz. ResultOutbox)
    [SupportedOSPlatform("windows")]
    internal sealed class TaskResults
    {
        private readonly ResultOutbox _outbox;
        private readonly Func<string?> _hwId;

        // hwId: gönderim anındaki cihaz kimliği (pc_name)
        public TaskResults(ResultOutbox outbox, Func<string?> hwId)
        {
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
        }

        public Task SendAsync(int taskId, string output, int exitCode) => _outbox.SendResultAsync(taskId, Result(taskId, output, exitCode));

        // Göndermeden saklar; sonra değiştirip gönderir (bkz. ResultOutbox.Store, SendStoredAsync)
        public void Store(int taskId, string output, int exitCode) => _outbox.Store(taskId, Result(taskId, output, exitCode));

        public Task SendStoredAsync(int taskId, string output, int exitCode) => _outbox.SendStoredAsync(taskId, Result(taskId, output, exitCode));

        private object Result(int taskId, string output, int exitCode) =>
            new { type = "result", pc_name = _hwId(), task_id = taskId, output, exit_code = exitCode };
    }
}
