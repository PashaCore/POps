using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // update_agent: arka planda AgentUpdate'e (komut döngüsünü bekletmez; emir kopyalanır, belge bu işleyiciden sonra
    // kapanır). update_result_ack: güncelleme sonucu onayı (bkz. UpdateResultReporter).
    [SupportedOSPlatform("windows")]
    internal sealed class UpdateHandler : ICommandHandler
    {
        private readonly HttpClient _httpClient;
        private readonly string _serverUrl;
        private readonly Func<Dictionary<string, object>, Task<bool>> _reportProgress;

        // reportProgress: update_progress gönderimi (bkz. Worker.ReportUpdateProgressAsync)
        public UpdateHandler(HttpClient httpClient, string serverUrl, Func<Dictionary<string, object>, Task<bool>> reportProgress)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _serverUrl = serverUrl;
            _reportProgress = reportProgress ?? throw new ArgumentNullException(nameof(reportProgress));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "update_agent", "update_result_ack" };

        public Task HandleAsync(ServerCommand command)
        {
            if (command.Action == "update_agent")
            {
                JsonElement update = command.Root.Clone();
                _ = Task.Run(() => AgentUpdate.HandleUpdateCommandAsync(update, _httpClient, _serverUrl, _reportProgress), CancellationToken.None);
            }
            else HandleUpdateResultAck(command.Root);
            return Task.CompletedTask;
        }

        // Sunucu sonucu kaydetti: result_id bekleyen sonuçla eşleşiyorsa dosya kenara alınır; eşleşmiyorsa beklemeye devam
        private static void HandleUpdateResultAck(JsonElement root)
        {
            string pending = AgentUpdate.PendingResultId();
            if (UpdateResultReporter.Acknowledges(root, pending))
            {
                AgentUpdate.MarkResultReported();
                POpsHelpers.Log("UPDATE", $"Sunucu güncelleme sonucunu onayladı ({pending}).");
            }
            else if (pending != null)
                POpsHelpers.Log("UPDATE", "Sunucunun onayı bekleyen güncelleme sonucuyla eşleşmiyor; sonuç saklanmaya devam ediyor.");
        }
    }
}
