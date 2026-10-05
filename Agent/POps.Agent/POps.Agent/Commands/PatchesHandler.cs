using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Windows Update: arka planda yürür, komut döngüsünü bekletmez (bkz. PatchManager). Sunucunun patches modülü kapalıysa
    // capability_denied.
    [SupportedOSPlatform("windows")]
    internal sealed class PatchesHandler : ICommandHandler
    {
        private readonly PatchManager _patches;
        private readonly CapabilityGate _gate;

        public PatchesHandler(PatchManager patches, CapabilityGate gate)
        {
            _patches = patches ?? throw new ArgumentNullException(nameof(patches));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "scan_updates", "install_updates" };

        public async Task HandleAsync(ServerCommand command)
        {
            if (!_gate.ModuleEnabled(AgentModules.Patches))
                await _gate.DenyAsync("patches", command.Action, reason: AgentModules.DisabledReason);
            else if (command.Action == "scan_updates") _patches.RequestScan();
            else
            {
                string? scope = command.Root.TryGetProperty("scope", out var scProp) && scProp.ValueKind == JsonValueKind.String ? scProp.GetString() : null;
                _patches.RequestInstall(scope);
            }
        }
    }
}
