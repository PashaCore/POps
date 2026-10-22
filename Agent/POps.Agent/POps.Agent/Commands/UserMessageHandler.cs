using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace POpsAgent
{
    // user_message: kullanıcıya mesaj (bkz. UserMessages). Sunucu bunu yalnızca X-Agent-Features'ta duyurulduğu için
    // gönderir.
    [SupportedOSPlatform("windows")]
    internal sealed class UserMessageHandler : ICommandHandler
    {
        private readonly UserMessages _messages;

        public UserMessageHandler(UserMessages messages)
        {
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { UserMessages.ActionName };

        public Task HandleAsync(ServerCommand command) => _messages.HandleAsync(command.Root, command.Stopping);
    }
}
