using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace POps.Shared
{
    // Tepsi, bağlandığı "POpsTrayPipe" borusunun gerçekten servise ait olduğunu denetler: borunun sahibi SYSTEM ya
    // da Administrators olmalı (servis LocalSystem olarak oluşturur). Servis çalışmıyorken ya da hızlı kullanıcı
    // değiştirmede başka bir oturum aynı adla boru açıp tepsiye sahte komut (kilit ekranı, ekran yakalama, uzaktan
    // girdi) gönderemesin. Sahip okunamazsa bağlanılmaz: saldırgan okuma iznini kaldırarak denetimi atlatamasın.
    // Servisin borusu etkileşimli kullanıcıya ReadWrite verir; bu, sahibi okumak için gereken ReadPermissions'ı içerir.
    [SupportedOSPlatform("windows")]
    public static class PipeOwner
    {
        private static readonly SecurityIdentifier LocalSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        public static bool IsTrustedOwner(SecurityIdentifier owner) =>
            owner != null && (owner.Equals(LocalSystem) || owner.Equals(Administrators));

        // Dönen: güvenilir mi; owner: okunan sahip (loglamak için) ya da okunamama nedeni
        public static bool Check(PipeStream pipe, out string owner)
        {
            try
            {
                PipeSecurity security = pipe.GetAccessControl();
                var sid = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                owner = sid?.Value ?? "(yok)";
                return IsTrustedOwner(sid);
            }
            catch (Exception ex)
            {
                owner = $"okunamadı: {ex.GetType().Name}";
                return false;
            }
        }
    }
}
