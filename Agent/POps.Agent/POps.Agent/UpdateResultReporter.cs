using System;
using System.Text.Json;

#nullable disable

namespace POpsAgent
{
    // update_result'ın sunucuya iletilmesi (S20). Eskiden mesaj gider gitmez update-result.json kenara alınıyordu;
    // sunucu kaydı yazmadan çökerse sonuç kayboluyordu. Sözleşme:
    //  * Sunucu ajan kaydı tamamlanınca {"action":"server_info","version":..,"features":["update_result_ack"]} gönderir
    //    (eski sunucu göndermez).
    //  * update_result mesajında "result_id": update-result.json ham baytlarının SHA-256'sı (küçük hex, ilk 32 karakter).
    //  * Sunucu kaydı yazınca {"action":"update_result_ack","result_id":".."} gönderir.
    // Her yeni bağlantıda "onay destekleniyor" bayrağı sıfırlanır; 15 sn server_info beklenir. Gelmezse eski sunucu
    // sayılır: gönder ve kenara al. Destekliyorsa: gönder, kenara alma; onay gelmezse bağlantı açıkken en çok 60 sn'de
    // bir yeniden gönder; result_id eşleşen onay gelince kenara al. Yerel olay 1030 bundan bağımsız, bir kez yazılır.
    public sealed class UpdateResultReporter
    {
        public const string AckFeature = "update_result_ack";
        public static readonly TimeSpan ServerInfoWait = ServerHandshake.ServerInfoWait;
        public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);

        // server_info ve 15 sn kuralı görev sonucu onayıyla (ResultSpool) ortaktır: Worker aynı nesneyi verir
        public UpdateResultReporter(ServerHandshake handshake = null) => Handshake = handshake ?? new ServerHandshake();

        public ServerHandshake Handshake { get; }

        public enum Step
        {
            // Gönderilecek sonuç yok ya da az önce gönderildi
            Nothing,
            // Bağlantı yeni: sunucunun onayı destekleyip desteklemediği henüz bilinmiyor
            Wait,
            // Eski sunucu: gönder ve kenara al (eski davranış)
            SendAndMarkReported,
            // Onaylı sunucu: gönder, dosya onaya kadar kalır
            SendAndKeep,
        }

        private readonly object _gate = new object();
        private string _sentId;
        private DateTime _sentUtc;

        // Saat el sıkışmayla ortak (testlerde değiştirilir)
        internal Func<DateTime> UtcNow { get => Handshake.UtcNow; set => Handshake.UtcNow = value; }

        public bool AckSupported => Handshake.Supports(AckFeature) == true;

        public void OnConnected()
        {
            Handshake.OnConnected();
            lock (_gate) _sentId = null;
        }

        // {"action":"server_info","features":[...]}
        public void OnServerInfo(JsonElement message) => Handshake.OnServerInfo(message);

        public Step Next(string resultId)
        {
            if (string.IsNullOrEmpty(resultId)) return Step.Nothing;
            bool? supported = Handshake.Supports(AckFeature);
            if (supported == null) return Step.Wait;
            if (supported == false) return Step.SendAndMarkReported;
            lock (_gate)
            {
                if (_sentId == resultId && UtcNow() - _sentUtc < ResendInterval) return Step.Nothing;
                return Step.SendAndKeep;
            }
        }

        public void Sent(string resultId)
        {
            lock (_gate)
            {
                _sentId = resultId;
                _sentUtc = UtcNow();
            }
        }

        // {"action":"update_result_ack","result_id":".."}: bekleyen sonucun kimliğiyle eşleşiyor mu
        public static bool Acknowledges(JsonElement message, string pendingResultId)
        {
            if (string.IsNullOrEmpty(pendingResultId) || message.ValueKind != JsonValueKind.Object) return false;
            return message.TryGetProperty("result_id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), pendingResultId, StringComparison.Ordinal);
        }
    }
}
