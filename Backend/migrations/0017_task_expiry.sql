-- 0017: dördüncü inceleme.
-- expires_at: bu andan sonra başlatılmayacak görev (zamanlanmış görevler; çevrimdışı cihaz geri dönünce gece
--   için konmuş bir "kapat" komutu sabah çalışmasın). Süresi dolan bekleyen görev "Expired" olur.
-- schedule_id: görevi açan zamanlanmış görev; aynı cihaz için bekleyen bir çalışması varken yenisi eklenmez.
-- agent_started_at: görev gönderildiğinde ajan sürecinin başlangıç zamanı (ajanın bildirdiği değer). Yeniden
--   bağlanınca bu değer değiştiyse ajan yeniden başlamıştır; ajan ve sunucu saatleri karşılaştırılmaz.
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS expires_at TIMESTAMPTZ;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS schedule_id INTEGER;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS agent_started_at DOUBLE PRECISION;
CREATE INDEX IF NOT EXISTS idx_tasks_schedule_pending ON tasks (schedule_id, target_pc)
    WHERE schedule_id IS NOT NULL AND status IN ('Pending', 'Paused');
