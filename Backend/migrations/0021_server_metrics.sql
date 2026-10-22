-- 0021: sunucu ölçüm geçmişi (Sistem → Genel bakış grafikleri). Zamanlayıcı dakikada bir satır yazar: bağlı ajan ve
-- panel, işlemci, bellek ve disk doluluğu (%), veritabanı boyutu ve süreç belleği (MB), o dakikadaki API isteği ve 5xx
-- yanıtı. 30 günden eski satırlar saatte bir silinir (pops/server_metrics.py). Görev grafiği için görevler oluşturulma
-- zamanına göre de dizinlenir.
CREATE TABLE IF NOT EXISTS server_metrics (
    ts TIMESTAMPTZ PRIMARY KEY,
    agents INTEGER,
    panels INTEGER,
    cpu_pct REAL,
    mem_pct REAL,
    disk_pct REAL,
    db_mb REAL,
    rss_mb REAL,
    requests INTEGER,
    errors INTEGER
);
CREATE INDEX IF NOT EXISTS idx_tasks_created ON tasks (created_at);
