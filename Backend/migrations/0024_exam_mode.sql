-- 0024: sınav modu (pops/exams.py, docs/api.md "Exam mode"). Bir sınıfta en fazla bir süren sınav (ended_at NULL):
-- ajanlar izin listesi dışındaki ağ trafiğini keser, tepside mesaj gösterir, isteğe bağlı programları engeller ve
-- until_at'te (çevrimdışı da olsa) kendiliğinden çıkar. Kayıt silinmez: sınav geçmişi (/api/exams) buradan okunur.
-- end_reason: admin (panelden bitirildi), expired (süresi doldu), lab_deleted (sınıf silindi), module_off (modül
-- kapatıldı).
CREATE TABLE IF NOT EXISTS exam_sessions (
    id          SERIAL PRIMARY KEY,
    lab_name    TEXT NOT NULL,
    allow_list  JSONB NOT NULL DEFAULT '[]',
    until_at    TIMESTAMPTZ NOT NULL,
    message     TEXT NOT NULL DEFAULT '',
    block_apps  JSONB NOT NULL DEFAULT '[]',
    reason      TEXT NOT NULL,
    started_by  TEXT NOT NULL,
    started_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_by    TEXT,
    ended_at    TIMESTAMPTZ,
    end_reason  TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS exam_sessions_one_active ON exam_sessions (lab_name) WHERE ended_at IS NULL;
CREATE INDEX IF NOT EXISTS exam_sessions_started ON exam_sessions (started_at DESC);

-- Sınavın her bilgisayardaki durumu: sunucunun gönderdiği (sent_at: son gönderim, first_sent_at: ilk gönderim,
-- released_at: sınav bitince ya da bilgisayar sınıftan çıkınca enabled:false gönderildi) ve ajanın bildirdiği
-- (exam_state: enabled, since, until; entered_at ilk "sınavda", left_at sınav sürerken ilk "çıktı", denied_at
-- yerel yetenek kapalı olduğu için reddetti).
CREATE TABLE IF NOT EXISTS exam_devices (
    exam_id       INTEGER NOT NULL REFERENCES exam_sessions (id) ON DELETE CASCADE,
    pc_name       TEXT NOT NULL,
    first_sent_at TIMESTAMPTZ,
    sent_at       TIMESTAMPTZ,
    released_at   TIMESTAMPTZ,
    reported_at   TIMESTAMPTZ,
    enabled       BOOLEAN,
    agent_since   TIMESTAMPTZ,
    agent_until   TIMESTAMPTZ,
    entered_at    TIMESTAMPTZ,
    left_at       TIMESTAMPTZ,
    denied_at     TIMESTAMPTZ,
    PRIMARY KEY (exam_id, pc_name)
);
CREATE INDEX IF NOT EXISTS exam_devices_pc ON exam_devices (pc_name);
