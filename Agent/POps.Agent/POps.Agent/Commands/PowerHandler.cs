using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace POpsAgent
{
    // power: uzaktan güç işlemi (bkz. PowerActions). Sunucu bunu yalnızca X-Agent-Features'ta duyurulduğu için gönderir.
    [SupportedOSPlatform("windows")]
    internal sealed class PowerHandler : ICommandHandler
    {
        private readonly PowerActions _power;

        public PowerHandler(PowerActions power)
        {
            _power = power ?? throw new ArgumentNullException(nameof(power));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { PowerActions.ActionName };

        public Task HandleAsync(ServerCommand command) => _power.HandleAsync(command.Root, command.Stopping);
    }
}
