using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Sunucu modülü denetimi ve ret bildirimi; Worker'ın ve işleyicilerin tek örneği (dakikada bir sınırı tüm eylemlerde
    // ortaktır). Yerel yetenekler bugünkü gibi AgentCapabilities'ten okunur.
    [SupportedOSPlatform("windows")]
    internal sealed class CapabilityGate
    {
        private readonly Func<object, Task<bool>> _send;
        private readonly Func<string, bool> _moduleEnabled;
        private readonly TimeProvider _clock;

        // send: komut kanalına gönderim (dönen: gönderildi mi); moduleEnabled: sunucu modülü açık mı (AgentModules.IsEnabled)
        public CapabilityGate(Func<object, Task<bool>> send, Func<string, bool> moduleEnabled, TimeProvider clock)
        {
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _moduleEnabled = moduleEnabled ?? throw new ArgumentNullException(nameof(moduleEnabled));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

        // Sunucunun bu bilgisayarın laboratuvarında açık tuttuğu modül (bkz. AgentModules)
        public bool ModuleEnabled(string module) => _moduleEnabled(module);

        // Kapalı bir yeteneğe gelen istek loglanır ve sunucuya "capability_denied" olarak bildirilir. Uzaktan fare
        // hareketi gibi sık gelen istekler için aynı yetenek/eylem en çok dakikada bir bildirilir; görev (task_id) ya da
        // dosya aktarımı (transfer_id) reddi her seferinde gider (sunucu o görevi / aktarımı kapatır).
        private readonly Dictionary<string, DateTime> _lastDenialNotice = new Dictionary<string, DateTime>();

        public async Task DenyAsync(string capability, string action, int? taskId = null, string? reason = null, string? transferId = null)
        {
            string key = $"{capability}/{action}/{reason}";
            lock (_lastDenialNotice)
            {
                if (taskId == null && transferId == null && _lastDenialNotice.TryGetValue(key, out DateTime last) && UtcNow - last < TimeSpan.FromMinutes(1)) return;
                _lastDenialNotice[key] = UtcNow;
            }
            if (reason == null)
                POpsHelpers.Log("POLICY", $"[GÜVENLİK] {action} reddedildi: {capability} bu cihazda kapalı (yetenek politikası).", true);
            else if (reason == AgentModules.DisabledReason)
                POpsHelpers.Log("POLICY", $"{action} reddedildi: {capability} modülü bu bilgisayarın laboratuvarında kapalı.", true);
            var notice = new Dictionary<string, object> { ["type"] = "capability_denied", ["capability"] = capability, ["action"] = action };
            if (taskId != null) notice["task_id"] = taskId.Value;
            if (transferId != null) notice["transfer_id"] = transferId;
            if (reason != null) notice["reason"] = reason;
            await _send(notice);
        }
    }
}
