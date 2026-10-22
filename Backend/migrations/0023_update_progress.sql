-- 0023: ajan güncellemesinin ara adımları (pops/update_tracking.py). Ajan update_agent'i aldıktan sonra sonuç
-- gelene kadar update_progress bildirir (alındı, indirildi, doğrulandı, kurulum başladı, Windows Installer meşgul,
-- kuruluyor; docs/api.md). Son adım gönderimin satırında durur: sunucu yeniden başlasa da panel nerede kaldığını
-- gösterir. Sonuç gelince ya da gönderim unutulunca satırla birlikte gider; yeni gönderim adımı sıfırlar.
-- Numara 0023: 0022'yi başka bir dal kullanabilir (çalıştırıcı uygulanmamış her dosyayı ad sırasıyla uygular).
ALTER TABLE pending_updates ADD COLUMN IF NOT EXISTS stage TEXT;
ALTER TABLE pending_updates ADD COLUMN IF NOT EXISTS detail TEXT;
ALTER TABLE pending_updates ADD COLUMN IF NOT EXISTS attempt INTEGER;
ALTER TABLE pending_updates ADD COLUMN IF NOT EXISTS attempt_of INTEGER;
ALTER TABLE pending_updates ADD COLUMN IF NOT EXISTS stage_at TIMESTAMPTZ;
