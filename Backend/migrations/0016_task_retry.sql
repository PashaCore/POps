-- 0016: yeniden deneme yeni görev kaydı açar (bkz. routers/tasks.py _retry). retry_of = denenen eski görev; aynı
-- görevin süren bir yeniden denemesi varsa ikincisi açılmaz. Yeni durum "Denied": ajan komutu yetenek politikası
-- (terminal kapalı) yüzünden çalıştırmadı.
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS retry_of INTEGER;
CREATE INDEX IF NOT EXISTS idx_tasks_retry_of ON tasks (retry_of) WHERE retry_of IS NOT NULL;
