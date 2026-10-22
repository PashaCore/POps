-- 0015: 0.1.14 güvenilirlik ve performans.

-- 2FA: kullanılan son TOTP zaman adımı. Aynı kod (ya da daha eski bir kod) geçerlilik penceresinde ikinci kez
-- kabul edilmez (bkz. pops/security.py consume_totp).
ALTER TABLE users ADD COLUMN IF NOT EXISTS totp_last_step BIGINT;

-- Takılı görev zaman aşımı (35 dk, pops/scheduler.py reap_stuck_tasks) dispatched_at'e bakar. 0014'ten önce
-- "Running" olmuş görevlerde bu alan boş; sayaç şimdiden başlar.
UPDATE tasks SET dispatched_at = NOW() WHERE status = 'Running' AND dispatched_at IS NULL;

-- Sık sorgular için indeksler: raporlar (zaman aralığı), cihaz etkinlik geçmişi, bypass kodu sayacı, görev geçmişi.
CREATE INDEX IF NOT EXISTS idx_agent_logs_v2_ts ON agent_logs_v2 ("timestamp");
CREATE INDEX IF NOT EXISTS idx_agent_logs_v2_pc ON agent_logs_v2 (pc_name, id);
CREATE INDEX IF NOT EXISTS idx_device_audit_hw_action ON device_audit_logs (hw_id, action, "timestamp");
CREATE INDEX IF NOT EXISTS idx_device_audit_action_ts ON device_audit_logs (action, "timestamp");
CREATE INDEX IF NOT EXISTS idx_tasks_pc_created ON tasks (target_pc, created_at);
CREATE INDEX IF NOT EXISTS idx_enterprise_audit_pc ON enterprise_audit_logs (target_pc, start_time);

-- Ajan güncellemesinin izi (S20, pops/update_tracking.py): gönderilmiş ve sonucu beklenen güncellemeler (sunucu
-- yeniden başlasa da "sonuç gelmedi" uyarısı kaybolmasın) ve kaydedilmiş sonuçlar (ajan sonucu onay gelene kadar
-- yeniden gönderir; aynı sonuç ikinci kez kaydedilmez).
CREATE TABLE IF NOT EXISTS pending_updates (
    pc_name TEXT PRIMARY KEY,
    version TEXT,
    sent_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE TABLE IF NOT EXISTS update_results (
    pc_name     TEXT NOT NULL,
    result_id   TEXT NOT NULL,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (pc_name, result_id)
);

-- Ajan bağlantısının son kopuşu: ne zaman ve neden (WebSocket kapanış kodu/sebebi). Panel cihaz ayrıntısında
-- gösterir; "Offline" cihazın ağ mı, servis mi, sunucu mu yüzünden koptuğu anlaşılır.
ALTER TABLE clients ADD COLUMN IF NOT EXISTS last_disconnect_at TIMESTAMPTZ;
ALTER TABLE clients ADD COLUMN IF NOT EXISTS last_disconnect_reason TEXT;

-- Tanınmayan cihaz bağlanınca kimlik kurtarma adayları yalnızca UUID ya da BIOS seri numarasıyla aranır
-- (pops/dna.py reconcile_device); karşılaştırma büyük/küçük harf ve baştaki/sondaki boşluk duyarsızdır.
CREATE INDEX IF NOT EXISTS idx_clients_dna_uuid_norm ON clients (lower(btrim(dna_uuid)));
CREATE INDEX IF NOT EXISTS idx_clients_dna_bios_norm ON clients (lower(btrim(dna_bios)));
