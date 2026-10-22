using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace POpsAgent
{
    // set_identity: sunucunun verdiği yeni cihaz kimliği identity.key'e yazılır (boşsa yok sayılır; bkz.
    // Worker.UpdateIdentityFile, açılıştaki donanım bağı da aynı yolu kullanır)
    [SupportedOSPlatform("windows")]
    internal sealed class IdentityHandler : ICommandHandler
    {
        private readonly Action<string?> _updateIdentity;

        public IdentityHandler(Action<string?> updateIdentity) =>
            _updateIdentity = updateIdentity ?? throw new ArgumentNullException(nameof(updateIdentity));

        public IReadOnlyList<string> Actions { get; } = new[] { "set_identity" };

        public Task HandleAsync(ServerCommand command)
        {
            _updateIdentity(command.Root.GetProperty("new_hw_id").GetString());
            return Task.CompletedTask;
        }
    }
}
