-- 0009: bildirimler, zamanlanmış görevler, yazılım envanteri, Windows güncelleme (yama) durumu.
-- Hepsi yeni tablo; mevcut veriye dokunulmaz (idempotent).

-- Panel zili + e-posta/webhook bildirimleri. Yalnızca sunucunun kendi karar verdiği olaylar
-- yazılır (ajanın /api/logs ile gönderdiği risk seviyesi bildirim üretmez).
CREATE TABLE IF NOT EXISTS notifications (
    id             SERIAL PRIMARY KEY,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    event          TEXT NOT NULL,          -- ör. update_problem, policy_alert, enroll_denied, test
    severity       TEXT NOT NULL,          -- critical | high | medium | info
    pc_name        TEXT,
    title          TEXT NOT NULL,
    detail         TEXT,
    channels       TEXT,                   -- gönderildiği kanallar: "email,webhook"
    delivery_error TEXT,
    is_read        BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE INDEX IF NOT EXISTS idx_notifications_created ON notifications (created_at DESC);

-- Zamanlanmış görevler: vakti gelince hedef cihazlar için normal görev kuyruğuna (tasks) eklenir.
CREATE TABLE IF NOT EXISTS scheduled_tasks (
    id            SERIAL PRIMARY KEY,
    name          TEXT NOT NULL,
    command       TEXT NOT NULL,
    target_mode   TEXT NOT NULL,           -- ALL | LAB | PC
    targets       TEXT NOT NULL DEFAULT '[]',   -- JSON liste (lab adları ya da HW- kimlikleri)
    schedule_type TEXT NOT NULL,           -- once | daily | weekly
    run_at        TIMESTAMPTZ,             -- once
    time_of_day   TEXT,                    -- "HH:MM", sunucu saat dilimi (daily/weekly)
    weekdays      TEXT,                    -- "1,2,3,4,5" (1 = Pazartesi), weekly
    enabled       BOOLEAN NOT NULL DEFAULT TRUE,
    next_run      TIMESTAMPTZ,
    last_run      TIMESTAMPTZ,
    last_result   TEXT,
    created_by    TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_scheduled_tasks_next ON scheduled_tasks (next_run) WHERE enabled;

-- Kurulu yazılımlar (ajan her envanter turunda listenin tamamını gönderir, sunucu değiştirir).
CREATE TABLE IF NOT EXISTS device_software (
    pc_name      TEXT NOT NULL,
    name         TEXT NOT NULL,
    version      TEXT NOT NULL DEFAULT '',
    publisher    TEXT,
    install_date TEXT,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (pc_name, name, version)
);
CREATE INDEX IF NOT EXISTS idx_device_software_name ON device_software (lower(name));

-- Windows Update durumu (ajanın son taraması).
CREATE TABLE IF NOT EXISTS device_patch_status (
    pc_name          TEXT PRIMARY KEY,
    pending_count    INTEGER NOT NULL DEFAULT 0,
    pending_security INTEGER NOT NULL DEFAULT 0,
    pending_critical INTEGER NOT NULL DEFAULT 0,
    reboot_required  BOOLEAN NOT NULL DEFAULT FALSE,
    last_search      TIMESTAMPTZ,
    last_install     TIMESTAMPTZ,
    updates          TEXT NOT NULL DEFAULT '[]',   -- JSON: [{kb, title, severity, categories}]
    last_result      TEXT,                         -- son kurulum sonucu (ajan bildirir)
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);
