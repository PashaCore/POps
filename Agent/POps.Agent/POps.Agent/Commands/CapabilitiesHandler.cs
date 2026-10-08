using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Sunucunun "set_capabilities" isteği: yalnızca kapatma uygulanır (bkz. AgentCapabilities). Vision kapandıysa
    // süren yayın hemen durdurulur. Son durum sunucuya "capabilities" olarak bildirilir. Sınav yeteneği kapandıysa
    // süren sınav biter (exam_state ile; afterReport, bkz. Worker.EndExamIfCapabilityOffAsync).
    [SupportedOSPlatform("windows")]
    internal sealed class CapabilitiesHandler : ICommandHandler
    {
        private readonly VisionSession _vision;
        private readonly PowerActions _power;
        private readonly Func<TrayPipeServer?> _trayPipe;
        private readonly Func<object, Task<bool>> _send;
        private readonly Action<LocalAuditEvent> _audit;
        private readonly Func<Task> _afterReport;

        // Tepsi borusu servis çalışırken kurulur: her kullanımda okunur. afterReport: "capabilities" gönderildikten sonra
        public CapabilitiesHandler(VisionSession vision, PowerActions power, Func<TrayPipeServer?> trayPipe,
            Func<object, Task<bool>> send, Action<LocalAuditEvent> audit, Func<Task> afterReport)
        {
            _vision = vision ?? throw new ArgumentNullException(nameof(vision));
            _power = power ?? throw new ArgumentNullException(nameof(power));
            _trayPipe = trayPipe ?? throw new ArgumentNullException(nameof(trayPipe));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _afterReport = afterReport ?? throw new ArgumentNullException(nameof(afterReport));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "set_capabilities" };

        public async Task HandleAsync(ServerCommand command)
        {
            JsonElement request = command.Root;
            var changed = AgentCapabilities.ApplyServerRequest(request);
            foreach (string capability in changed.Disabled)
                _audit(LocalAudit.CapabilityChanged(capability.Replace("_enabled", "", StringComparison.Ordinal), true, false));
            // Eş önbelleği kapandıysa önbellek silinir, sunum durur
            if (changed.Disabled.Contains(AgentCapabilities.PeerCacheKey)) await PeerCache.SyncAsync();
            if (!AgentCapabilities.VisionEnabled && _vision.StreamActive)
            {
                _trayPipe()?.SendCommandToDesktop("STOP_CAPTURE");
                await _vision.DisconnectVisionTunnelAsync();
            }
            // Güç işlemleri kapandıysa süren geri sayım da durur
            if (!AgentCapabilities.PowerEnabled && _power.CancelForDisabledCapability())
                POpsHelpers.Log("AGENT", "Güç işlemleri kapatıldı; süren geri sayım durduruldu.");
            await _send(AgentCapabilities.StatusMessage());
            await _afterReport();
        }
    }
}
