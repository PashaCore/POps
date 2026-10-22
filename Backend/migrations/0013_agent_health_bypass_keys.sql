-- 0013: ajan sağlık özeti ve cihaz başına çevrimdışı bypass anahtarı (ajan 0.1.12+).
--
-- clients.agent_health: heartbeat'teki "agent_health" bloğu (bkz. pops/agent_health.py). Her heartbeat'te
--   üzerine yazılır; bildirmeyen (eski) ajanda NULL'dur.
--
-- agent_bypass_keys: sunucunun her cihaz için ürettiği 32 baytlık anahtar (base64url). Kod, cihaz
--   çevrimdışıyken üretildiği için ham anahtar saklanır; özeti değil. Veritabanına yazabilen biri zaten
--   karantinayı panelden kaldırabildiği için bu, yeni bir yetki vermez. Ajan anahtarı aldığını SHA-256
--   parmak iziyle onaylar (confirmed_at); onay gelene kadar panel eski ortak kodu da gösterir.
ALTER TABLE clients ADD COLUMN IF NOT EXISTS agent_health JSONB;

CREATE TABLE IF NOT EXISTS agent_bypass_keys (
    pc_name TEXT PRIMARY KEY,
    secret TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    issued_at TIMESTAMP NOT NULL DEFAULT NOW(),
    confirmed_at TIMESTAMP
);
