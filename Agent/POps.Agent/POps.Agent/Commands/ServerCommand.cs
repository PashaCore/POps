using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;

namespace POpsAgent
{
    // Sunucudan gelen tek komut (bkz. CommandDispatcher). Root yalnızca HandleAsync dönene kadar geçerlidir: işi arka
    // planda süren işleyici ya değerleri önce okur (execute) ya da kökü kopyalar (update_agent).
    internal sealed class ServerCommand
    {
        // "type": "remote_input" mesajında "remote_input"
        public required string Action { get; init; }
        public required JsonElement Root { get; init; }
        // Ham mesaj: tepsiye olduğu gibi iletilir (start_vision_session, remote_input)
        public required string Raw { get; init; }
        // Mesajın geldiği soket (thumbnail yanıtı o soketten gider); testlerde null
        public ClientWebSocket? Connection { get; init; }
        public CancellationToken Stopping { get; init; }
    }
}
