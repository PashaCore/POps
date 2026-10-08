using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace POpsAgent
{
    // get_hardware: açılışta toplanan donanım envanteri POST /api/inventory/{hw_id} ile gönderilir (envanter henüz
    // toplanmadıysa ya da toplanamadıysa hiçbir şey yapılmaz).
    [SupportedOSPlatform("windows")]
    internal sealed class InventoryHandler : ICommandHandler
    {
        private readonly Func<object?> _inventory;
        private readonly string _serverUrl;
        private readonly Func<string?> _hwId;
        private readonly AgentHealthTelemetry _health;

        // inventory: açılışın yavaş adımında (WMI) toplanan envanter, yoksa null; kimlik her kullanımda okunur
        public InventoryHandler(Func<object?> inventory, string serverUrl, Func<string?> hwId, AgentHealthTelemetry health)
        {
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _serverUrl = serverUrl;
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _health = health ?? throw new ArgumentNullException(nameof(health));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "get_hardware" };

        public Task HandleAsync(ServerCommand command) => SendHardwareInfoAsync();

        private async Task SendHardwareInfoAsync()
        {
            object? inventory = _inventory();
            if (inventory == null) return;
            try
            {
                string json = JsonSerializer.Serialize(inventory);
                using var request = new HttpRequestMessage(HttpMethod.Post, _serverUrl.TrimEnd('/') + $"/api/inventory/{_hwId()}")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                AgentCredentials.AddHttpAuth(request, _hwId());
                // Kimlik başlıkları taşıyan istek yönlendirme izlemeyen istemciyle gider (bkz. AgentHttp)
                using var response = await AgentHttp.Client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    POpsHelpers.Log("AGENT", $"Donanım envanteri gönderilemedi: HTTP {(int)response.StatusCode}.", true);
                    return;
                }
                POpsHelpers.Log("AGENT", "Donanım envanteri sunucuya gönderildi.");
                _health.InventoryUploaded();
            }
            catch (Exception ex)
            {
                _health.RecordError("inventory", ex.Message);
                POpsHelpers.Log("AGENT", $"Donanım envanteri gönderilemedi: {ex.Message}", true);
            }
        }
    }
}
