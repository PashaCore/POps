using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace POpsAgent
{
    // set_secret: enroll jetonuyla kaydolan ajanın kalıcı secret'ı. set_bypass_secret: cihaza özel çevrimdışı bypass
    // anahtarı (yalnızca cihaz secret'ıyla doğrulanmış bağlantıdan; bkz. BypassSecretCommand).
    [SupportedOSPlatform("windows")]
    internal sealed class SecretsHandler : ICommandHandler
    {
        private readonly Func<string?> _hwId;
        private readonly Func<HardwareBinding> _binding;
        private readonly Func<SoftwareReporter?> _software;
        private readonly Func<bool> _commandUsesDeviceSecret;
        private readonly Func<object, Task<bool>> _send;
        private readonly Action<LocalAuditEvent> _audit;

        // Kimlik, donanım bağı ve yazılım envanteri çalışırken değişebilir (set_identity, testler); her kullanımda okunur.
        // commandUsesDeviceSecret: komut bağlantısı cihaz secret'ıyla mı kuruldu (bkz. Worker.ApplyAuthHeaders)
        public SecretsHandler(Func<string?> hwId, Func<HardwareBinding> binding, Func<SoftwareReporter?> software,
            Func<bool> commandUsesDeviceSecret, Func<object, Task<bool>> send, Action<LocalAuditEvent> audit)
        {
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _binding = binding ?? throw new ArgumentNullException(nameof(binding));
            _software = software ?? throw new ArgumentNullException(nameof(software));
            _commandUsesDeviceSecret = commandUsesDeviceSecret ?? throw new ArgumentNullException(nameof(commandUsesDeviceSecret));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "set_secret", "set_bypass_secret" };

        public async Task HandleAsync(ServerCommand command)
        {
            if (command.Action == "set_secret") { HandleSetSecret(command.Root); }
            else { await HandleSetBypassSecretAsync(command.Root); }
        }

        // Enroll jetonuyla kaydolan ajana sunucu kalıcı secret'ı bir kez gönderir.
        private void HandleSetSecret(JsonElement root)
        {
            string? secret = root.TryGetProperty("secret", out var sProp) && sProp.ValueKind == JsonValueKind.String ? sProp.GetString() : null;
            if (!AgentCredentials.IsWellFormed(secret))
            {
                POpsHelpers.Log("AGENT", "[GÜVENLİK] set_secret yok sayıldı: secret biçimi geçersiz.", true);
                return;
            }

            // Anahtar bu donanıma bağlanır; yeni kayıtta sunucunun yazılım listesi boştur, son gönderim unutulur
            _binding().Bind(_hwId(), "set_secret");
            _software()?.ForgetLastReport();
            if (AgentCredentials.SaveSecret(secret, _hwId()))
            {
                AgentCredentials.ForgetEnrollToken();
                POpsHelpers.Log("AGENT", "Cihaz secret'ı alındı ve güvenli depoya yazıldı; sonraki bağlantılar secret ile doğrulanacak.");
            }
            else
            {
                POpsHelpers.Log("AGENT", "Cihaz secret'ı alındı ancak diske yazılamadı; servis yeniden başlayana kadar bellekte tutuluyor.", true);
            }
        }

        private async Task HandleSetBypassSecretAsync(JsonElement root)
        {
            string? secret = root.TryGetProperty("secret", out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() : null;
            bool saved = BypassSecretCommand.Process(secret, _commandUsesDeviceSecret(),
                AgentCredentials.SaveDeviceBypassSecret,
                message => POpsHelpers.Log("AGENT", message,
                    message.StartsWith("[GÜVENLİK]", StringComparison.Ordinal) || message.EndsWith("yazılamadı.", StringComparison.Ordinal)),
                out string fingerprint);
            if (saved)
            {
                _audit(LocalAudit.BypassSecretReceived(fingerprint));
                await _send(new { type = "bypass_secret_ack", fingerprint });
            }
        }
    }
}
