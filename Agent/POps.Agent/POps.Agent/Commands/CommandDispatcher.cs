using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Komut kanalındaki sunucu mesajı -> işleyici tablosu (Worker.HandleServerMessageAsync'teki eski if/else zincirinin
    // yerine). "type": "remote_input" mesajı action'a bakılmadan kendi işleyicisine gider. Tanınmayan action yok sayılır.
    // Hiçbir istisna yakalanmaz: çözümlenemeyen JSON, JSON dizisi ya da metin olmayan type/action eskisi gibi çağırana
    // (ReceiveCommandsAsync) gider ve orada loglanır.
    internal sealed class CommandDispatcher
    {
        internal const string RemoteInput = "remote_input";

        private readonly Dictionary<string, ICommandHandler> _byAction = new Dictionary<string, ICommandHandler>(StringComparer.Ordinal);
        private readonly ICommandHandler _remoteInput;

        public CommandDispatcher(IEnumerable<ICommandHandler> handlers, ICommandHandler remoteInput)
        {
            ArgumentNullException.ThrowIfNull(handlers);
            _remoteInput = remoteInput ?? throw new ArgumentNullException(nameof(remoteInput));
            foreach (ICommandHandler handler in handlers)
                foreach (string action in handler.Actions)
                    _byAction.Add(action, handler);   // aynı eylem iki işleyicide: açılışta (ve testte) hata
        }

        // Tablodaki eylemler ("remote_input" türü hariç)
        public IReadOnlyCollection<string> Actions => _byAction.Keys;

        // Eylemin işleyicisi; tabloda yoksa null
        public ICommandHandler? HandlerFor(string action) => _byAction.GetValueOrDefault(action);

        public async Task DispatchAsync(string message, ClientWebSocket? connection, CancellationToken stopping)
        {
            using JsonDocument doc = JsonDocument.Parse(message);
            JsonElement root = doc.RootElement;
            bool remoteInput = root.TryGetProperty("type", out JsonElement type) && type.GetString() == RemoteInput;
            string? action = remoteInput ? RemoteInput : root.TryGetProperty("action", out JsonElement a) ? a.GetString() : "";
            if (action == null) return;
            ICommandHandler? handler = remoteInput ? _remoteInput : HandlerFor(action);
            if (handler == null) return;
            await handler.HandleAsync(new ServerCommand { Action = action, Root = root, Raw = message, Connection = connection, Stopping = stopping });
        }
    }
}
