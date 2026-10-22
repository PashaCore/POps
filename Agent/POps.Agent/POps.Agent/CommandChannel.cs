using System;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Komut soketine (/ws/agent) yazım. Heartbeat, komut mesajları, ekran önizlemesi ve vision_rejected aynı sokete bir
    // kilit altında tek tek yazılır. Soketi CommandConnection her bağlanma denemesinde yenisiyle değiştirir.
    [SupportedOSPlatform("windows")]
    internal sealed class CommandChannel
    {
        private readonly SemaphoreSlim _wsCommandLock;
        private ClientWebSocket? _commandWs;

        // commandLock: komut soketinin yazma kilidi (Worker'ındır, servis boyunca yaşar)
        public CommandChannel(SemaphoreSlim commandLock)
        {
            _wsCommandLock = commandLock ?? throw new ArgumentNullException(nameof(commandLock));
        }

        // Testler içindir: giden komut mesajları sokete yazılmaz, buraya verilir (dönen: gönderildi mi)
        internal Func<object, Task<bool>>? SendOverride { get; set; }

        // O anki komut soketi (CommandConnection her bağlanma denemesinde yenisini koyar)
        internal ClientWebSocket? Socket { get => _commandWs; set => _commandWs = value; }

        // Dönen: mesaj o anki komut soketine yazıldı mı
        internal async Task<bool> TrySendAsync(object payload)
        {
            if (SendOverride != null) return await SendOverride(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await _wsCommandLock.WaitAsync();
            try
            {
                if (_commandWs == null || _commandWs.State != WebSocketState.Open) return false;
                await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                POpsHelpers.Log("AGENT", $"Sunucuya mesaj gönderilemedi: {ex.Message}", true);
                return false;
            }
            finally { _wsCommandLock.Release(); }
        }

        // Heartbeat'in kendi yolu (SendOverride'a gitmez): kilit servis belirteciyle beklenir, yalnızca soket açıkken yazılır,
        // hata bağlantı döngüsüne gider. Yazıldıysa sent kilit bırakılmadan çağrılır.
        internal async Task SendHeartbeatAsync(byte[] bytes, Action sent, CancellationToken token)
        {
            await _wsCommandLock.WaitAsync(token);
            try
            {
                if (_commandWs != null && _commandWs.State == WebSocketState.Open)
                {
                    await _commandWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                    sent();
                }
            }
            finally { _wsCommandLock.Release(); }
        }

        // Doğrudan bir sokete yazılan mesajlar (SendOverride'a gitmez, hata çağırana gider). Ekran önizlemesi isteğin geldiği
        // sokete gider ve kilidi süresiz bekler; vision_rejected o anki komut soketine gider ve kilidi en çok 2 sn bekler
        // (alınamazsa düşer). Soket kilit alındıktan sonra okunur; yalnızca açıksa yazılır.
        internal async Task SendOnAsync(Func<ClientWebSocket?> socket, byte[] bytes, int lockTimeoutMs = Timeout.Infinite)
        {
            if (await _wsCommandLock.WaitAsync(lockTimeoutMs))
            {
                try
                {
                    ClientWebSocket? ws = socket();
                    if (ws?.State == WebSocketState.Open) await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally { _wsCommandLock.Release(); }
            }
        }
    }
}
