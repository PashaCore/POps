using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Henüz Worker'da duran bir komut grubu: Worker'ın mevcut metodunu saran işleyici. Grup sonraki bölme adımında kendi
    // sınıfına taşınınca kaldırılır (bkz. docs/design/worker-split.md).
    internal sealed class DelegateHandler : ICommandHandler
    {
        private readonly Func<ServerCommand, Task> _handle;

        public DelegateHandler(Func<ServerCommand, Task> handle, params string[] actions)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            Actions = actions;
        }

        public IReadOnlyList<string> Actions { get; }

        public Task HandleAsync(ServerCommand command) => _handle(command);
    }
}
