using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace POpsAgent
{
    // wake_peer: aynı ağdaki bir bilgisayara Wake-on-LAN paketi (sunucunun wol modülü açıksa; bkz. WakeOnLan)
    [SupportedOSPlatform("windows")]
    internal sealed class WakeOnLanHandler : ICommandHandler
    {
        private readonly CapabilityGate _gate;

        public WakeOnLanHandler(CapabilityGate gate) => _gate = gate ?? throw new ArgumentNullException(nameof(gate));

        public IReadOnlyList<string> Actions { get; } = new[] { "wake_peer" };

        public async Task HandleAsync(ServerCommand command)
        {
            if (!_gate.ModuleEnabled(AgentModules.Wol)) await _gate.DenyAsync("wol", command.Action, reason: AgentModules.DisabledReason);
            else { WakeOnLan.Send(command.Root.GetProperty("mac").GetString()); }
        }
    }
}
