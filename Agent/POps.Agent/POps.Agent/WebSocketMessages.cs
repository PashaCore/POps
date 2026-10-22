using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Bir WebSocket mesajını, parçalara (fragment / tampondan büyük mesaj) bölünmüş olsa da EndOfMessage'a kadar
    // birleştirerek okur. Eskiden tek ReceiveAsync okunuyordu: uzun dağıtım betikleri kesilip JSON'u bozuluyor
    // ve sessizce yok sayılıyordu. Üst sınır, sunucudan gelen sınırsız mesajın belleği tüketmesini engeller.
    public static class WebSocketMessages
    {
        public sealed class TooLargeException : Exception
        {
            public TooLargeException(int limit) : base($"WebSocket mesajı {limit} baytı aşıyor") { }
        }

        // Close çerçevesinde (null, Close) döner; metin mesajında (metin, Text). Sınırı aşan mesajın kalan
        // parçaları okunup atılır (bir sonraki mesaja karışmasın), ardından TooLargeException fırlatılır.
        public static async Task<(string Text, WebSocketMessageType Type)> ReceiveTextAsync(WebSocket ws, byte[] buffer, int maxBytes, CancellationToken token)
        {
            using var message = new MemoryStream();
            bool tooLarge = false;
            while (true)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return (null, WebSocketMessageType.Close);
                if (!tooLarge && message.Length + result.Count > maxBytes)
                {
                    tooLarge = true;
                    message.SetLength(0);
                }
                if (!tooLarge) message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                if (tooLarge) throw new TooLargeException(maxBytes);
                return (Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), result.MessageType);
            }
        }
    }
}
