using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // "type": "remote_input": ekran önizlemesi (get_thumbnail; yanıt geldiği soketten gider) ya da tepsiye giden
    // uzaktan fare/klavye. Yalnızca bu bilgisayara gelen; Vision yeteneği, modülü ve girdi için onaylı oturum gerekir
    // (bkz. VisionSession.VisionDenial). Önizleme: tepsiye CAPTURE_SNAPSHOT, yanıtı tepsinin bir sonraki JPEG karesi
    // (tepsi borusu karesi önce TryCompleteSnapshot'a verir; bkz. TrayMessageRouter).
    [SupportedOSPlatform("windows")]
    internal sealed class RemoteInputHandler : ICommandHandler
    {
        private readonly VisionSession _vision;
        private readonly CapabilityGate _gate;
        private readonly Func<string?> _hwId;
        private readonly Func<TrayPipeServer?> _trayPipe;
        private readonly CommandChannel _channel;
        private TaskCompletionSource<byte[]>? _thumbnailTcs;

        // Kimlik set_identity ile değişir, tepsi borusu servis çalışırken kurulur: her kullanımda okunur. channel: komut
        // soketine yazım (önizleme yanıtı heartbeat ve diğer mesajlarla aynı kilit altında yazılır)
        public RemoteInputHandler(VisionSession vision, CapabilityGate gate, Func<string?> hwId, Func<TrayPipeServer?> trayPipe,
            CommandChannel channel)
        {
            _vision = vision ?? throw new ArgumentNullException(nameof(vision));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _trayPipe = trayPipe ?? throw new ArgumentNullException(nameof(trayPipe));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { CommandDispatcher.RemoteInput };

        public async Task HandleAsync(ServerCommand command)
        {
            string message = command.Raw;
            JsonElement root = command.Root;
            ClientWebSocket? ws = command.Connection;
            CancellationToken stoppingToken = command.Stopping;

            string? targetDevice = root.TryGetProperty("device", out var devProp) ? devProp.GetString() : "";
            if (targetDevice != _hwId()) return;

            string? act = root.TryGetProperty("action", out var actProp) ? actProp.GetString() : "";
            // Ekran önizlemesi ve uzaktan fare/klavye Vision yeteneğidir
            var (denial, denialReason) = _vision.VisionDenial(VisionSession.IsInputEvent(root));
            if (denial != null)
            {
                await _gate.DenyAsync(denial, string.IsNullOrEmpty(act) ? "remote_input" : act, reason: denialReason);
                return;
            }
            if (act == "get_thumbnail")
            {
                _ = Task.Run(async () =>
                {
                    byte[]? img = await CaptureSnapshotAsync(TimeSpan.FromSeconds(5));
                    if (img != null && img.Length > 0)
                    {
                        var payload = new { type = "thumbnail", hw_id = _hwId(), image = Convert.ToBase64String(img) };
                        byte[] b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                        // İsteğin geldiği sokete; kilit süresiz beklenir (bkz. CommandChannel.SendOnAsync)
                        await _channel.SendOnAsync(() => ws, b);
                    }
                }, stoppingToken);
            }
            else
            {
                _trayPipe()?.SendCommandToDesktop(message);
            }
        }

        private async Task<byte[]?> CaptureSnapshotAsync(TimeSpan timeout)
        {
            if (_trayPipe() == null) return null;
            var tcs = new TaskCompletionSource<byte[]>();
            var old = Interlocked.Exchange(ref _thumbnailTcs, tcs);
            old?.TrySetCanceled();
            try
            {
                _trayPipe()?.SendCommandToDesktop("CAPTURE_SNAPSHOT");
                using var cts = new CancellationTokenSource(timeout);
                cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);
                return await tcs.Task;
            }
            catch { return null; }
            finally { Interlocked.CompareExchange(ref _thumbnailTcs, null, tcs); }
        }

        // Tepsiden JPEG kare: bekleyen bir önizleme varsa onun yanıtıdır (dönen: true), yoksa yayının karesidir
        internal bool TryCompleteSnapshot(byte[] jpegBytes)
        {
            var tcs = Interlocked.Exchange(ref _thumbnailTcs, null);
            if (tcs != null)
            {
                tcs.TrySetResult(jpegBytes);
                return true;
            }
            return false;
        }
    }
}
