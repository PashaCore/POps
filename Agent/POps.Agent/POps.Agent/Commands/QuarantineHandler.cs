using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace POpsAgent
{
    // lockdown / unlock: sunucunun karantina emri (kilit ekranı + ağ yalıtımı; bkz. QuarantineControl)
    [SupportedOSPlatform("windows")]
    internal sealed class QuarantineHandler : ICommandHandler
    {
        private readonly Func<QuarantineControl> _quarantine;
        private readonly string _serverUrl;
        private readonly Func<string?> _hwId;

        // Karantina denetimi testlerde sahtesiyle değiştirilir, kimlik set_identity ile değişir: her kullanımda okunur
        public QuarantineHandler(Func<QuarantineControl> quarantine, string serverUrl, Func<string?> hwId)
        {
            _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
            _serverUrl = serverUrl;
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "lockdown", "unlock" };

        public async Task HandleAsync(ServerCommand command)
        {
            if (command.Action == "lockdown")
            {
                string? reason = command.Root.TryGetProperty("reason", out var rProp) && rProp.ValueKind == JsonValueKind.String ? rProp.GetString() : null;
                await _quarantine().LockdownAsync(reason);
            }
            else
            {
                // Panel cihazı açık gösterir; yalıtım kaldırılamadıysa denetim kaydı bunu söyler
                if (!await _quarantine().UnlockAsync("server"))
                    await AgentHttp.PostJsonAsync(_serverUrl, AgentHttp.DevicePath("/api/logs/", _hwId()), _hwId(), QuarantineControl.UnlockFailedLog(), "Karantina kaldırma hatası");
            }
        }
    }
}
