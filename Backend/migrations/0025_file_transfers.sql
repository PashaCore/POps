-- 0025: dosya aktarımı (pops/routers/files.py, pops/filestore.py). Yönetici bir bilgisayara dosya gönderir (push) ya
-- da bilgisayardan dosya alır (pull); her hedef bilgisayar için bir satır. Dosyanın içeriği veritabanına yazılmaz:
-- sunucudaki dosya FILES_DIR altında, sunucunun verdiği adla (storage_path) durur. Ajanın indirme/yükleme jetonu
-- yalnızca SHA-256 özetiyle tutulur, tek kullanımlıktır (token_used_at) ve 1 saatte dolar.
-- Durumlar: sent (ajana gönderildi), downloading / uploading (jeton kullanıldı, aktarım sürüyor), done, rejected,
-- failed, expired (jeton kullanılmadan doldu).
-- Numara 0025: 0022 ve 0024'ü başka dallar kullanıyor (çalıştırıcı uygulanmamış her dosyayı ad sırasıyla uygular).
CREATE TABLE IF NOT EXISTS file_transfers (
    id BIGSERIAL PRIMARY KEY,
    transfer_id TEXT NOT NULL UNIQUE,
    direction TEXT NOT NULL CHECK (direction IN ('push', 'pull')),
    pc_name TEXT NOT NULL,
    batch_id TEXT,                          -- aynı yüklemeyle birden çok bilgisayara gönderim (tek dosya)
    name TEXT,                              -- dosya adı (push: temizlenmiş yükleme adı; pull: yolun son parçası)
    size BIGINT,
    sha256 TEXT,
    dest TEXT,                              -- push: public_desktop | inbox
    path TEXT,                              -- pull: istenen yol; push: ajanın yazdığı yol (file_result)
    max_size BIGINT,                        -- pull: kabul edilen en büyük boyut
    allow_exec BOOLEAN NOT NULL DEFAULT FALSE,
    any_profile BOOLEAN NOT NULL DEFAULT FALSE,
    reason TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'sent',
    detail TEXT,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    finished_at TIMESTAMPTZ,
    token_hash TEXT,
    token_expires_at TIMESTAMPTZ,
    token_used_at TIMESTAMPTZ,
    storage_path TEXT,                      -- FILES_DIR altındaki dosya adı; silinince NULL
    purged_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_file_transfers_pc ON file_transfers (pc_name, id DESC);
CREATE INDEX IF NOT EXISTS idx_file_transfers_status ON file_transfers (status, created_at);
CREATE INDEX IF NOT EXISTS idx_file_transfers_batch ON file_transfers (batch_id);

-- Ajan "capabilities" iletisinde files_enabled bildirir: TRUE = dosya aktarımı açık, FALSE = bilgisayarda kapatılmış,
-- NULL = ajan bu özelliği bilmiyor (eski sürüm). Sunucu yalnızca TRUE olan bilgisayara dosya gönderir ya da ister.
ALTER TABLE clients ADD COLUMN IF NOT EXISTS cap_files_enabled BOOLEAN;
