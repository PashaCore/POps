using System;
using POps.Shared;

#nullable disable

namespace POpsAgent
{
    public static class BypassSecretCommand
    {
        public static bool Process(string secret, bool authenticatedWithDeviceSecret, Func<string, bool> save,
            Action<string> log, out string fingerprint)
        {
            fingerprint = null;
            if (!authenticatedWithDeviceSecret)
            {
                log?.Invoke("[GÜVENLİK] set_bypass_secret yok sayıldı: komut bağlantısı cihaz anahtarıyla doğrulanmadı.");
                return false;
            }
            if (!DeviceBypassSecret.TryDecode(secret, out byte[] key))
            {
                log?.Invoke("[GÜVENLİK] set_bypass_secret yok sayıldı: anahtar biçimi geçersiz.");
                return false;
            }
            if (save == null || !save(secret))
            {
                log?.Invoke("Bypass cihaz anahtarı güvenli depoya yazılamadı.");
                return false;
            }

            fingerprint = DeviceBypassSecret.Fingerprint(key);
            log?.Invoke("Bypass cihaz anahtarı güvenli depoya yazıldı.");
            return true;
        }
    }
}
