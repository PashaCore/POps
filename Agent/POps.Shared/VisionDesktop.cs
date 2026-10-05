#nullable enable

using System;

namespace POps.Shared
{
    // Vision: girdi masaüstü (ekranda görünen, klavye ve farenin gittiği masaüstü) tepsinin çalıştığı masaüstü mü?
    // UAC onayı (güvenli masaüstü), Ctrl+Alt+Del, kilit ve oturum açma ekranı "Winlogon" masaüstündedir; onu yalnızca
    // SYSTEM açabilir. Kullanıcı olarak çalışan tepsi o sırada girdi masaüstünü açamaz (erişim reddi), DXGI ve GDI de
    // onu yakalayamaz. Ayrıntı: docs/vision.md "Secure desktop".
    public static class VisionDesktop
    {
        // inputOpened: OpenInputDesktop başarılı mı. Adlardan biri okunamadıysa eskisi gibi yakalanır (yanlışlıkla
        // bildirim resmi göstermektense gerçek görüntü denenir).
        public static bool IsOwn(bool inputOpened, string? inputName, string? ownName)
        {
            if (!inputOpened) return false;
            if (string.IsNullOrEmpty(inputName) || string.IsNullOrEmpty(ownName)) return true;
            return string.Equals(inputName, ownName, StringComparison.OrdinalIgnoreCase);
        }
    }

    public enum VisionDesktopStep
    {
        // Masaüstü tepsinin: her zamanki gibi yakala
        Capture,
        // Masaüstü az önce gitti: bildirim resmi gönder (ve logla)
        Enter,
        // Hâlâ yok ve bildirim yinelenmeli (süre doldu ya da tam kare istendi)
        Notice,
        // Hâlâ yok, bildirim yakın zamanda gitti: bir şey gönderme
        Wait,
        // Masaüstü geri geldi: kaynakları yeniden aç (DXGI çoğaltması geçersizdir), tam kare gönder (ve logla)
        Resume,
    }

    // Her kareden önce sorulur (saf karar; tepsi her iki yolda da kullanır: eski JPEG yayını ve Vision v2). Masaüstü
    // görünmüyorken donmuş son kare yerine bildirim resmi gider: ilk seferde, sonra NoticeRepeat'te bir (sonradan
    // bağlanan görüntüleyici de görsün) ve görüntüleyici tam kare isteyince (v2: düşen kare, ekran/ölçek değişimi).
    public sealed class VisionDesktopGate
    {
        public static readonly TimeSpan NoticeRepeat = TimeSpan.FromSeconds(5);

        private DateTime _lastNoticeUtc;

        public bool Away { get; private set; }

        public VisionDesktopStep Next(bool ownDesktop, bool needFull, DateTime nowUtc)
        {
            if (ownDesktop)
            {
                if (!Away) return VisionDesktopStep.Capture;
                Away = false;
                return VisionDesktopStep.Resume;
            }
            if (!Away)
            {
                Away = true;
                _lastNoticeUtc = nowUtc;
                return VisionDesktopStep.Enter;
            }
            if (needFull || nowUtc - _lastNoticeUtc >= NoticeRepeat || nowUtc < _lastNoticeUtc)
            {
                _lastNoticeUtc = nowUtc;
                return VisionDesktopStep.Notice;
            }
            return VisionDesktopStep.Wait;
        }
    }
}
