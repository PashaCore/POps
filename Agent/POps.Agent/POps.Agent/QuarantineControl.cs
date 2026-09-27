using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Karantina: sunucunun lockdown/unlock komutları ve tepsiden gelen çevrimdışı bypass kodu aynı yoldan geçer.
    // Geçerli bypass kodu, sunucudan gelen unlock ile aynı şeyi yapar: kilit ekranını kapatır ve ağ yalıtımını
    // kaldırır (önceden yalnızca yalıtım kalkıyor, kilit ekranı sunucu unlock gönderene kadar açık kalıyordu).
    public sealed class QuarantineControl
    {
        private readonly Action<string> _toTray;
        private readonly Func<Task> _enableIsolation;
        private readonly Func<Task> _disableIsolation;
        private readonly OfflineBypass _bypass;

        public QuarantineControl(Action<string> toTray, Func<Task> enableIsolation, Func<Task> disableIsolation, OfflineBypass bypass = null)
        {
            _toTray = toTray;
            _enableIsolation = enableIsolation;
            _disableIsolation = disableIsolation;
            _bypass = bypass ?? new OfflineBypass();
        }

        public OfflineBypass Bypass => _bypass;

        public static string LockdownMessage(string reason) =>
            JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = "lockdown", ["reason"] = string.IsNullOrWhiteSpace(reason) ? "Belirtilmedi" : reason });

        // source: "server" (panelden unlock) ya da "bypass" (çevrimdışı bypass kodu); tepsi bildirimi buna göre değişir
        public static string UnlockMessage(string source) =>
            JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = "unlock", ["source"] = source });

        public async Task LockdownAsync(string reason)
        {
            _toTray(LockdownMessage(reason));
            await _enableIsolation();
        }

        public async Task UnlockAsync(string source)
        {
            _toTray(UnlockMessage(source));
            DnsPolicyMonitor.ResetViolations();
            await _disableIsolation();
        }

        // Tepsiden gelen kod. Geçerliyse kilit ekranı kapanır ve yalıtım kalkar; sonuç tepsiye BYPASS_SUCCESS /
        // BYPASS_FAILED olarak döner. bypassSecret yoksa bypass kapalıdır.
        public async Task<OfflineBypass.Result> HandleBypassAsync(string token, string hwId, string bypassSecret, DateTime localDate)
        {
            if (string.IsNullOrEmpty(bypassSecret))
            {
                POpsHelpers.Log("AGENT", $"Offline Bypass devre dışı: BypassSecret tanımlı değil ({SecureStore.Dir}\\{AgentCredentials.BypassSecretFileName}).", true);
                _toTray("BYPASS_FAILED");
                return OfflineBypass.Result.Rejected;
            }

            OfflineBypass.Result result = _bypass.Attempt(token, hwId, bypassSecret, localDate);
            switch (result)
            {
                case OfflineBypass.Result.Accepted:
                    POpsHelpers.Log("AGENT", "Offline Bypass kodu doğrulandı; kilit ekranı ve karantina kaldırılıyor.");
                    _toTray("BYPASS_SUCCESS");
                    await UnlockAsync("bypass");
                    return result;
                case OfflineBypass.Result.Locked:
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] Offline Bypass kilitli ({_bypass.LockedUntilUtc.ToLocalTime():HH:mm} saatine kadar); deneme değerlendirilmedi.", true);
                    break;
                case OfflineBypass.Result.LockedOut:
                    POpsHelpers.Log("AGENT", $"[GÜVENLİK] {OfflineBypass.MaxFailures} hatalı Offline Bypass denemesi; bypass {_bypass.LockedUntilUtc.ToLocalTime():HH:mm} saatine kadar kilitlendi.", true);
                    break;
                default:
                    POpsHelpers.Log("AGENT", $"Offline Bypass kodu hatalı ({_bypass.Failures}/{OfflineBypass.MaxFailures}).", true);
                    break;
            }
            _toTray("BYPASS_FAILED");
            return result;
        }
    }
}
