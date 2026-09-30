using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

#nullable disable

namespace POpsAgent
{
    // Karantina: sunucunun lockdown/unlock komutları, DNS eşiğindeki otomatik karantina ve tepsiden gelen çevrimdışı
    // bypass kodu aynı yoldan geçer.
    //  * Kilit durumu diskte tutulur (C:\POpsData\secure\lockdown.json ve ağ yalıtımı açıksa isolation.json): tepsi
    //    her bağlandığında (servis açılışı, tepsinin yeniden başlaması, oturum kapatma/açma) kilit ekranı yeniden
    //    gösterilir; tepsiyi Görev Yöneticisi'nden kapatmak kilidi kaldırmaz.
    //  * Geçerli bypass kodu unlock ile aynı şeyi yapar: ağ yalıtımını kaldırır ve kilit ekranını kapatır.
    //  * Yalıtım kaldırılamazsa kilit sürer ve kullanıcıya "kaldırıldı" denmez (UNLOCK_FAILED).
    public sealed class QuarantineControl
    {
        private readonly Action<string> _toTray;
        private readonly Func<Task<bool>> _enableIsolation;
        private readonly Func<Task<bool>> _disableIsolation;
        private readonly OfflineBypass _bypass;

        public QuarantineControl(Action<string> toTray, Func<Task<bool>> enableIsolation, Func<Task<bool>> disableIsolation, OfflineBypass bypass = null)
        {
            _toTray = toTray;
            _enableIsolation = enableIsolation;
            _disableIsolation = disableIsolation;
            _bypass = bypass ?? new OfflineBypass(statePath: SecureStore.PathOf(OfflineBypass.StateFileName));
        }

        public OfflineBypass Bypass => _bypass;

        public static string LockPath => SecureStore.PathOf("lockdown.json");

        public const string DefaultReason = "Belirtilmedi";
        // DNS eşiğindeki otomatik karantinanın kilit ekranındaki nedeni
        public const string AutoQuarantineReason = "DNS kural ihlali eşiği";

        // /api/logs olayları; sunucu event_type'a göre panelde karantina durumunu günceller ve bildirim gönderir
        public static AgentLogPayload AutoQuarantineLog(string reason) => new AgentLogPayload
        {
            LogType = "Security",
            Message = "Cihaz DNS kural ihlali eşiğinde kendini karantinaya aldı",
            EventType = "agent.auto_quarantine",
            Category = "security",
            Action = "auto_quarantine",
            RiskLevel = "high",
            Reason = reason ?? "",
        };

        public static AgentLogPayload UnlockFailedLog() => new AgentLogPayload
        {
            LogType = "Security",
            Message = "Karantina kaldırılamadı: ağ yalıtımı duruyor, kilit ekranı açık",
            EventType = "agent.unlock_failed",
            Category = "security",
            Action = "unlock_failed",
            RiskLevel = "high",
        };

        public static AgentLogPayload OfflineBypassLog() => new AgentLogPayload
        {
            LogType = "Security",
            Message = "Çevrimdışı bypass kodu ile kilit ekranı ve karantina kaldırıldı",
            EventType = "agent.offline_bypass",
            Category = "security",
            Action = "offline_bypass",
            RiskLevel = "medium",
        };

        public bool IsLocked => File.Exists(LockPath) || File.Exists(NetworkIsolation.StatePath);

        public string LockReason
        {
            get
            {
                try
                {
                    string json = SecureStore.Read(LockPath);
                    if (json != null)
                    {
                        using JsonDocument doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.GetString()))
                            return r.GetString();
                    }
                }
                catch { }
                return DefaultReason;
            }
        }

        public static string LockdownMessage(string reason) =>
            JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = "lockdown", ["reason"] = string.IsNullOrWhiteSpace(reason) ? DefaultReason : reason });

        // source: "server" (panelden unlock), "bypass" (çevrimdışı bypass kodu) ya da "sync" (tepsi bağlandı, kilit
        // yok: açık kalmış eski kilit ekranı sessizce kapanır); tepsinin bildirimi buna göre değişir
        public static string UnlockMessage(string source) =>
            JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = "unlock", ["source"] = source });

        // Kilit ekranı gösterilir ve ağ yalıtılır. Dönen: yalıtım uygulandı mı (uygulanamasa da kilit sürer).
        // İdempotent: sunucu bekleyen kilidi heartbeat'teki "quarantined" doğrulanana kadar yeniden gönderir. Zaten
        // yalıtılmışsa kurallar yeniden kurulmaz (PowerShell yok, kurallar bir an bile kalkmaz); yalnızca neden
        // güncellenir ve tepsi eşitlenir (açık kilit ekranı yinelenmez).
        public async Task<bool> LockdownAsync(string reason)
        {
            // Sunucunun yeniden gönderdiği kilit nedensiz olabilir: kayıtlı neden korunur
            reason = !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : IsLocked ? LockReason : DefaultReason;
            bool alreadyIsolated = File.Exists(NetworkIsolation.StatePath);
            try { SecureStore.WriteProtected(LockPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["reason"] = reason })); }
            catch (Exception ex) { POpsHelpers.Log("AGENT", $"Kilit durumu yazılamadı ({LockPath}): {ex.Message}", true); }
            // Ctrl+Alt+Del seçenekleri (Görev Yöneticisi, oturumu kapat, kullanıcı değiştir, ...) kilit sürerken kapalı
            KioskMode.Engage();
            _toTray(LockdownMessage(reason));
            if (alreadyIsolated) return true;
            bool isolated = await _enableIsolation();
            if (!isolated) POpsHelpers.Log("AGENT", "[GÜVENLİK] Kilit ekranı gösterildi ama ağ yalıtımı uygulanamadı.", true);
            return isolated;
        }

        // Ağ yalıtımı kaldırılır; başarılıysa kilit ekranı kapanır. Başarısızsa kilit sürer ve tepsi UNLOCK_FAILED alır.
        // İdempotent: kilit yokken gelen unlock (ör. sunucunun yeniden gönderdiği) PowerShell çalıştırmaz ve bildirim
        // göstermez; yalnızca açık kalmış eski bir kilit ekranı sessizce kapanır.
        public async Task<bool> UnlockAsync(string source)
        {
            if (!IsLocked)
            {
                KioskMode.Release();
                _toTray(UnlockMessage("sync"));
                return true;
            }
            bool removed = await _disableIsolation();
            if (!removed)
            {
                POpsHelpers.Log("AGENT", $"[GÜVENLİK] Karantina kaldırılamadı ({source}): ağ yalıtımı duruyor, kilit ekranı açık kalıyor.", true);
                _toTray("UNLOCK_FAILED");
                return false;
            }
            SecureStore.Delete(LockPath);
            KioskMode.Release();
            DnsPolicyMonitor.ResetViolations();
            _toTray(UnlockMessage(source));
            return true;
        }

        // Tepsi bağlandı: kilit durumunu tepsiyle eşitler
        public void SyncTray()
        {
            // Sonradan oturum açan kullanıcının kovanı da kapsanır; kilit yoksa karantinada oturumu kapatmış
            // kullanıcının bekleyen ayarları geri alınır
            KioskMode.Sync(IsLocked);
            if (IsLocked) _toTray(LockdownMessage(LockReason));
            else _toTray(UnlockMessage("sync"));
        }

        // Tepsiden gelen kod. Geçerliyse karantina kalkar (bkz. UnlockAsync); sonuç tepsiye BYPASS_SUCCESS /
        // BYPASS_FAILED olarak döner. bypassSecret yoksa bypass kapalıdır. Dönen: kod kabul edildi VE kilit kalktı mı
        public async Task<bool> HandleBypassAsync(string token, string hwId, string bypassSecret, DateTime localDate)
            => await HandleBypassAsync(token, hwId, bypassSecret, null, false, localDate);

        public async Task<bool> HandleBypassAsync(string token, string hwId, string legacySecret,
            string deviceSecret, bool deviceSecretPresent, DateTime localDate)
        {
            if ((!deviceSecretPresent && string.IsNullOrEmpty(legacySecret)) ||
                (deviceSecretPresent && !POps.Shared.DeviceBypassSecret.TryDecode(deviceSecret, out _)))
            {
                string reason = deviceSecretPresent ? "bypass.device okunamadı ya da biçimi geçersiz" : "BypassSecret tanımlı değil";
                POpsHelpers.Log("AGENT", $"Offline Bypass devre dışı: {reason}.", true);
                _toTray("BYPASS_FAILED");
                return false;
            }

            switch (_bypass.Attempt(token, hwId, legacySecret, deviceSecret, deviceSecretPresent, localDate))
            {
                case OfflineBypass.Result.Accepted:
                    POpsHelpers.Log("AGENT", "Offline Bypass kodu doğrulandı; kilit ekranı ve karantina kaldırılıyor.");
                    if (!await UnlockAsync("bypass")) return false;
                    _toTray("BYPASS_SUCCESS");
                    return true;
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
            return false;
        }
    }
}
