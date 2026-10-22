-- 0006: Ajan yetenek politikası (terminal/Vision aç-kapa) + gerçek çalışan sürüm.
--
-- Yetenek politikası fail-safe'tir: kaynak doğruluk ajandadır (SYSTEM-only capabilities.json,
-- MSI ile kurulur). Sunucu bir yeteneği yalnızca KAPATABİLİR; ajan sunucudan gelen "aç"ı yok
-- sayar (kalıcı açma = yeniden kurulum / offline-imzalı politika). Bu yüzden sunucu iki şey tutar:
--   cap_terminal_enabled / cap_vision_enabled     — ajanın SON BİLDİRDİĞİ güncel durum (NULL=bilinmiyor/eski ajan)
--   cap_*_disable_requested                        — yöneticinin KAPATMA isteği; ajan çevrimdışıyken
--                                                    kaydedilir, yeniden bağlanınca set_capabilities ile uygulanır.
-- running_version: update_result'ın bildirdiği, güncelleme/rollback sonrası GERÇEKTEN çalışan sürüm.
-- Bu dosya idempotenttir.

ALTER TABLE clients ADD COLUMN IF NOT EXISTS cap_terminal_enabled          BOOLEAN;
ALTER TABLE clients ADD COLUMN IF NOT EXISTS cap_vision_enabled            BOOLEAN;
ALTER TABLE clients ADD COLUMN IF NOT EXISTS cap_terminal_disable_requested BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE clients ADD COLUMN IF NOT EXISTS cap_vision_disable_requested   BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE clients ADD COLUMN IF NOT EXISTS running_version               TEXT;
