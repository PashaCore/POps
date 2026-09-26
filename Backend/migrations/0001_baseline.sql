-- POps veritabanı şeması: temel sürüm (0001)
--
-- Bu dosya, 2026-09-26 itibarıyla canlı veritabanının şemasını ve
-- Backend/server.py içindeki init_db() fonksiyonunun boş bir veritabanında
-- kurduğu şemayı birebir tanımlar. İkisi `pg_dump --schema-only` ile
-- karşılaştırılmış ve aynı olduğu doğrulanmıştır.
--
-- Şimdilik şemayı init_db() kurar; bu dosya başvuru ve ileride gelecek
-- migration sisteminin (Alembic) başlangıç noktasıdır. Şemaya yapılan her
-- değişiklik hem init_db()'ye hem de yeni bir 000N_*.sql dosyasına yazılmalıdır.
--
-- Bilinçli olarak olduğu gibi bırakılanlar (sonraki migration'larda ele alınacak):
--   * Zaman damgaları TEXT ('YYYY-MM-DD HH:MM:SS'); yalnızca bypass_tokens
--     timestamptz kullanıyor. timestamptz'ye geçiş veri dönüşümü gerektirir.
--   * agent_logs eski log tablosudur; loglar agent_logs_v2'ye yazılıyor
--     (USE_V2_SCHEMA). Eski satırlar taşınmadan tablo silinmeyecek.
--   * Tablolar arasında yabancı anahtar yok (ör. clients.lab_name ->
--     custom_labs, tasks.target_pc -> clients). Laboratuvar adı değişince
--     tutarlılığı şimdilik uygulama kodu sağlıyor (rename_lab / delete_lab).
--   * users.permissions JSON'u TEXT olarak tutuluyor.
--
-- Bu dosya idempotenttir: mevcut bir veritabanında çalıştırılması bir şey değiştirmez.

CREATE TABLE IF NOT EXISTS users (
    id            SERIAL PRIMARY KEY,
    username      TEXT UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,          -- yalnızca bcrypt kabul edilir
    role          TEXT DEFAULT 'admin',   -- superadmin | admin | viewer
    last_login    TEXT,
    permissions   TEXT DEFAULT '[]'       -- erişilebilen sayfaların JSON listesi
);

CREATE TABLE IF NOT EXISTS clients (
    pc_name          TEXT PRIMARY KEY,    -- HW- kimliği
    hostname         TEXT,
    lab_name         TEXT,
    last_seen        TEXT,
    status           TEXT,                -- 'Online' | 'Offline' | ajanın bildirdiği durum
    active_window    TEXT,
    boot_count       INTEGER DEFAULT 0,
    logged_user      TEXT DEFAULT '-',
    ip_address       TEXT,
    dna_uuid         TEXT,
    dna_bios         TEXT,
    dna_disk         TEXT,
    dna_mac          TEXT,
    dna_ram          TEXT,
    cap_ram_readable BOOLEAN DEFAULT TRUE,
    is_quarantined   BOOLEAN DEFAULT FALSE,
    display_name     TEXT
);
CREATE INDEX IF NOT EXISTS idx_dna_uuid ON clients (dna_uuid);

CREATE TABLE IF NOT EXISTS device_audit_logs (
    id          SERIAL PRIMARY KEY,
    hw_id       TEXT,
    action      TEXT,
    reason      TEXT,
    changes     TEXT,
    "timestamp" TEXT
);

CREATE TABLE IF NOT EXISTS lab_settings (
    lab_name    TEXT PRIMARY KEY,
    main_pc     TEXT,
    layout_json TEXT DEFAULT '{}'         -- oturma planı
);

CREATE TABLE IF NOT EXISTS global_settings (
    key   TEXT PRIMARY KEY,
    value TEXT
);
INSERT INTO global_settings (key, value) VALUES ('concurrent_limit', '5') ON CONFLICT (key) DO NOTHING;

CREATE TABLE IF NOT EXISTS packages (
    id      TEXT PRIMARY KEY,
    name    TEXT,
    type    TEXT,
    meta    TEXT,
    command TEXT,
    icon    TEXT,
    color   TEXT
);

CREATE TABLE IF NOT EXISTS custom_labs (
    lab_name TEXT PRIMARY KEY
);

CREATE TABLE IF NOT EXISTS hw_inventory (
    pc_name      TEXT PRIMARY KEY,
    hostname     TEXT,
    cpu          TEXT,
    ram          TEXT,
    motherboard  TEXT,
    gpu          TEXT,
    os_version   TEXT,
    ip_address   TEXT,
    mac_address  TEXT,
    disk_info    TEXT,
    last_updated TEXT
);

CREATE TABLE IF NOT EXISTS tasks (
    id          SERIAL PRIMARY KEY,
    target_pc   TEXT,                     -- HW- kimliği
    target_lab  TEXT,
    script_path TEXT,                     -- çalıştırılacak komut
    status      TEXT,                     -- Pending | Running | Completed | ...
    created_at  TEXT,
    output      TEXT
);

CREATE TABLE IF NOT EXISTS enterprise_audit_logs (   -- Vision oturumları
    session_id   TEXT PRIMARY KEY,
    admin_id     INTEGER,
    admin_name   TEXT,
    admin_role   TEXT,
    target_pc    TEXT,
    start_time   TEXT,
    end_time     TEXT,
    reason       TEXT,
    is_notified  BOOLEAN,
    is_mandatory BOOLEAN,
    status       TEXT
);

CREATE TABLE IF NOT EXISTS agent_logs (            -- eski log tablosu, yalnızca okunur
    id          SERIAL PRIMARY KEY,
    pc_name     TEXT,
    log_type    TEXT,
    message     TEXT,
    "timestamp" TEXT
);

CREATE TABLE IF NOT EXISTS agent_logs_v2 (
    id          SERIAL PRIMARY KEY,
    pc_name     TEXT,
    actor_id    TEXT,
    event_type  TEXT,
    category    TEXT,
    action      TEXT,
    risk_level  TEXT,
    reason      TEXT,
    message     TEXT,
    meta_data   JSONB,
    "timestamp" TEXT
);

CREATE TABLE IF NOT EXISTS agent_versions (
    pc_name     TEXT PRIMARY KEY,
    version     TEXT,
    last_update TEXT
);

CREATE TABLE IF NOT EXISTS bypass_tokens (         -- tek kullanımlık çevrimdışı kilit açma kodları
    id         SERIAL PRIMARY KEY,
    pc_name    TEXT NOT NULL,
    token      TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    is_used    BOOLEAN DEFAULT FALSE
);
