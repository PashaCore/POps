-- 0012: ajanın sunucu sertifikasını nasıl doğruladığı (capabilities mesajındaki "server_ca").
--   custom: cihazdaki kurum CA'sına (C:\POpsData\secure\server-ca.pem) zincirlenmeyen sertifika reddedilir
--   system: Windows'un güvenilen kök deposu kullanılır (Let's Encrypt ya da kurumun dağıttığı kök)
--   NULL:   ajan bildirmiyor (0.1.9 ve öncesi)
ALTER TABLE clients ADD COLUMN IF NOT EXISTS cap_server_ca TEXT;
