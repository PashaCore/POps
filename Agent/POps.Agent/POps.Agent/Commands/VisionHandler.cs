using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // start_stream (eski), start_vision_session, stop_stream: önce Vision yeteneği, sonra Vision modülü (bkz. VisionSession)
    [SupportedOSPlatform("windows")]
    internal sealed class VisionHandler : ICommandHandler
    {
        private readonly VisionSession _vision;
        private readonly CapabilityGate _gate;
        private readonly Func<TrayPipeServer?> _trayPipe;

        // Tepsi borusu servis çalışırken kurulur: her kullanımda okunur
        public VisionHandler(VisionSession vision, CapabilityGate gate, Func<TrayPipeServer?> trayPipe)
        {
            _vision = vision ?? throw new ArgumentNullException(nameof(vision));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _trayPipe = trayPipe ?? throw new ArgumentNullException(nameof(trayPipe));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "start_stream", "start_vision_session", "stop_stream" };

        public async Task HandleAsync(ServerCommand command)
        {
            string message = command.Raw;
            JsonElement root = command.Root;
            string action = command.Action;
            CancellationToken stoppingToken = command.Stopping;
            if ((action == "start_stream" || action == "start_vision_session") && !AgentCapabilities.VisionEnabled)
            {
                await _gate.DenyAsync("vision", action);
            }
            else if ((action == "start_stream" || action == "start_vision_session") && !_gate.ModuleEnabled(AgentModules.Vision))
            {
                await _gate.DenyAsync("vision", action, reason: AgentModules.DisabledReason);
            }
            else if (action == "start_stream")
            {
                await _vision.StartStreamAsync(root, stoppingToken);
            }
            else if (action == "stop_stream")
            {
                _trayPipe()?.SendCommandToDesktop("STOP_CAPTURE");
                await _vision.DisconnectVisionTunnelAsync();
            }
            else if (action == "start_vision_session")
            {
                _vision.RequestSession(message, root);
            }
        }
    }
}
