using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace POpsAgent
{
    // result_ack: görev sonucu sunucuda yazıldı (bkz. ResultSpool, ResultOutbox)
    [SupportedOSPlatform("windows")]
    internal sealed class ResultAckHandler : ICommandHandler
    {
        private readonly ResultOutbox _outbox;

        public ResultAckHandler(ResultOutbox outbox) => _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));

        public IReadOnlyList<string> Actions { get; } = new[] { "result_ack" };

        public Task HandleAsync(ServerCommand command)
        {
            _outbox.HandleResultAck(command.Root);
            return Task.CompletedTask;
        }
    }
}
