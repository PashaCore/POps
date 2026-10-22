using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace POpsAgent
{
    // server_info: sunucunun duyurduğu özellikler (15 sn kuralı; bkz. ServerHandshake). Ardından bağlantı sonrası
    // bildirim (exam_state; bkz. Worker.ReportExamStateOnConnectAsync).
    [SupportedOSPlatform("windows")]
    internal sealed class HandshakeHandler : ICommandHandler
    {
        private readonly ServerHandshake _handshake;
        private readonly Func<Task> _afterServerInfo;

        public HandshakeHandler(ServerHandshake handshake, Func<Task> afterServerInfo)
        {
            _handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
            _afterServerInfo = afterServerInfo ?? throw new ArgumentNullException(nameof(afterServerInfo));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "server_info" };

        public async Task HandleAsync(ServerCommand command)
        {
            _handshake.OnServerInfo(command.Root);
            await _afterServerInfo();
        }
    }
}
