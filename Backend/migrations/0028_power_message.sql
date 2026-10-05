-- 0028: güç komutları ve kullanıcıya mesaj (pops/power.py, docs/agent.md "power" ve "user_message").
-- tasks.kind = 'power' (payload {"op", "delay", "message"}) ya da 'user_message' (payload {"title", "text", "style",
-- "requires_ack"}); script_path okunur özettir, ajana gitmez ve mesaj metnini taşımaz. Sütunlar 0027 (winget) ile
-- aynıdır; burada da IF NOT EXISTS ile eklenir (hangi sırayla uygulanırsa uygulansın).
-- agent_versions.features: ajanın X-Agent-Features ile duyurduğu özellikler ("power", "message").
-- clients.platform: 0026 (Linux ajanı) ile aynı sütun; NULL = windows. Eski ajana kapatma/yeniden başlatma komutla
-- gider ve Linux ajanı yalnızca not içermeyen "shutdown /s|/r /f /t N" kalıbını tanır.
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS kind TEXT;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS payload JSONB;
ALTER TABLE agent_versions ADD COLUMN IF NOT EXISTS features TEXT[];
ALTER TABLE clients ADD COLUMN IF NOT EXISTS platform TEXT;
