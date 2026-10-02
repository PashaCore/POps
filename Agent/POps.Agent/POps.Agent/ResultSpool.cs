using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

#nullable disable

namespace POpsAgent
{
    // Görev sonuçları, sunucu veritabanına yazdığını onaylayana kadar diskte (S20'nin görev sonucu karşılığı).
    // Sözleşme: sunucu server_info features'ta "result_ack" duyurur; her result'ı yazınca {"action":"result_ack",
    // "task_id":N} gönderir; aynı sonucun ikinci kez gelmesi sunucuda zararsızdır.
    //  * Onaylı sunucuda giden her result C:\POpsData\secure\pending-results.json'a yazılır (yalnızca SYSTEM/
    //    Administrators; geçici dosya + rename), result_ack ile silinir; yeniden bağlanınca (server_info'dan sonra)
    //    onaysızlar yeniden gönderilir. Servis yeniden başlayınca dosya okunur.
    //  * En çok 20 sonuç: sınır dolunca en eskisi atılır ve loglanır (her sonucun çıktısı zaten sınırlı).
    //  * Eski sunucu (result_ack yok): Worker bellekteki kuyruğu kullanır (gönderilince silinir); diskte kalmışlar
    //    gönderilince silinir.
    public sealed class ResultSpool
    {
        public const string FileName = "pending-results.json";
        public const string AckFeature = "result_ack";
        public const int MaxResults = 20;

        public sealed class Entry
        {
            public int TaskId { get; set; }
            public JsonElement Result { get; set; }
        }

        private readonly object _gate = new object();
        private readonly string _path;
        private readonly List<Entry> _entries;
        // Bu bağlantıda gönderilmiş olanlar (onay bekleniyor); yeni bağlantıda sıfırlanır
        private readonly HashSet<int> _sent = new HashSet<int>();

        public ResultSpool(string path)
        {
            _path = path;
            _entries = Load(path);
        }

        public int Count { get { lock (_gate) return _entries.Count; } }

        public List<int> TaskIds() { lock (_gate) return _entries.Select(e => e.TaskId).ToList(); }

        public bool Contains(int taskId) { lock (_gate) return _entries.Any(e => e.TaskId == taskId); }

        public void Add(int taskId, object result)
        {
            JsonElement element = result is JsonElement e ? e.Clone() : JsonSerializer.SerializeToElement(result);
            lock (_gate)
            {
                _entries.RemoveAll(x => x.TaskId == taskId);
                _sent.Remove(taskId);
                _entries.Add(new Entry { TaskId = taskId, Result = element });
                while (_entries.Count > MaxResults)
                {
                    Entry dropped = _entries[0];
                    _entries.RemoveAt(0);
                    _sent.Remove(dropped.TaskId);
                    POpsHelpers.Log("AGENT", $"Onay bekleyen görev sonuçları sınırı ({MaxResults}) aşıldı; en eskisi atıldı (görev {dropped.TaskId}).", true);
                }
                Save();
            }
        }

        // result_ack: dönen, böyle bir sonuç bekliyor muydu
        public bool Remove(int taskId)
        {
            lock (_gate)
            {
                _sent.Remove(taskId);
                if (_entries.RemoveAll(x => x.TaskId == taskId) == 0) return false;
                Save();
                return true;
            }
        }

        public List<Entry> Unsent() { lock (_gate) return _entries.Where(e => !_sent.Contains(e.TaskId)).ToList(); }

        public List<Entry> All() { lock (_gate) return _entries.ToList(); }

        public void MarkSent(int taskId) { lock (_gate) _sent.Add(taskId); }

        public void OnConnected() { lock (_gate) _sent.Clear(); }

        private void Save()
        {
            try
            {
                if (_entries.Count == 0) SecureStore.Delete(_path);
                else SecureStore.WriteProtected(_path, JsonSerializer.Serialize(_entries));
            }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"{_path} yazılamadı; onay bekleyen sonuçlar yalnızca bellekte: {ex.Message}", true); }
        }

        private static List<Entry> Load(string path)
        {
            try
            {
                string text = SecureStore.Read(path);
                if (text == null) return new List<Entry>();
                List<Entry> entries = (JsonSerializer.Deserialize<List<Entry>>(text) ?? new List<Entry>())
                    .Where(e => e != null && e.Result.ValueKind == JsonValueKind.Object).ToList();
                if (entries.Count > 0) POpsHelpers.Log("AGENT", $"Önceki çalışmadan onay bekleyen {entries.Count} görev sonucu bulundu; sunucuya yeniden gönderilecek.");
                return entries.Skip(Math.Max(0, entries.Count - MaxResults)).ToList();
            }
            catch (JsonException ex)
            {
                POpsHelpers.Log("AGENT", $"{path} okunamadı ({ex.Message}); onay bekleyen sonuçlar yok sayıldı.", true);
                return new List<Entry>();
            }
        }
    }
}
