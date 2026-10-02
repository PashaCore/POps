-- 0014: 0.1.13 sağlamlaştırması.
--
-- Kayıt jetonları: veritabanında jetonun kendisi değil, yalnızca SHA-256 özeti (token_hash) ve panelde tanımak
-- için ilk 6 karakteri (token_hint) kalır. Jeton yalnızca üretildiği anda bir kez gösterilir.
ALTER TABLE enroll_tokens ADD COLUMN IF NOT EXISTS token_hash TEXT;
ALTER TABLE enroll_tokens ADD COLUMN IF NOT EXISTS token_hint TEXT;
UPDATE enroll_tokens
   SET token_hash = encode(sha256(convert_to(token, 'UTF8')), 'hex'), token_hint = left(token, 6)
 WHERE token IS NOT NULL AND token_hash IS NULL;
ALTER TABLE enroll_tokens ALTER COLUMN token DROP NOT NULL;
UPDATE enroll_tokens SET token = NULL WHERE token IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_enroll_tokens_token_hash ON enroll_tokens (token_hash);

-- Görevler: ajanın bildirdiği çıkış kodu ve görevin ajana gönderildiği an. Bağlantı kopup ajan geri gelince
-- görevin akıbeti buna göre ayrılır (bkz. routers/agents.py _settle_running_tasks).
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS exit_code INTEGER;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS dispatched_at TIMESTAMPTZ;
CREATE INDEX IF NOT EXISTS idx_tasks_active ON tasks (target_pc, id) WHERE status IN ('Pending', 'Running');

-- Yeni kurulumda (henüz hiç cihaz yokken) ajan kimlik zorlaması açık başlar. Mevcut kurulumların ayarına dokunulmaz.
INSERT INTO global_settings (key, value)
SELECT 'enforce_agent_auth', '1' WHERE NOT EXISTS (SELECT 1 FROM clients)
ON CONFLICT (key) DO NOTHING;
