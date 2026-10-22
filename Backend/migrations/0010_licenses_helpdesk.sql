-- 0010: lisans takibi ve yardım masası (destek talepleri). Yalnızca yeni tablolar (idempotent).

-- Lisans: yazılım envanterindeki adla eşleşen kurulumlar sayılır ve koltuk sayısıyla karşılaştırılır.
CREATE TABLE IF NOT EXISTS licenses (
    id            SERIAL PRIMARY KEY,
    name          TEXT NOT NULL,             -- ör. "Microsoft Office LTSC 2021"
    match_pattern TEXT NOT NULL,             -- yazılım adında aranan ifade (büyük/küçük harf duyarsız, "içerir")
    publisher     TEXT,                      -- isteğe bağlı yayıncı süzgeci ("içerir")
    seats         INTEGER,                   -- NULL = sınırsız (site/kampüs lisansı)
    license_type  TEXT NOT NULL DEFAULT 'per_device',   -- per_device | site | subscription
    expires_at    DATE,
    notes         TEXT,
    created_by    TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Yardım masası: talepler tepsiden (ajan) ya da panelden açılır.
CREATE TABLE IF NOT EXISTS tickets (
    id          SERIAL PRIMARY KEY,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    source      TEXT NOT NULL,               -- agent | panel
    pc_name     TEXT,                        -- HW- kimliği (varsa)
    reporter    TEXT,                        -- ajan: oturumdaki kullanıcı; panel: kaydı açan kişi
    category    TEXT NOT NULL DEFAULT 'diger',   -- donanim | yazilim | ag | yazici | hesap | diger
    subject     TEXT NOT NULL,
    body        TEXT,
    status      TEXT NOT NULL DEFAULT 'open',    -- open | in_progress | waiting | resolved | closed
    priority    TEXT NOT NULL DEFAULT 'normal',  -- low | normal | high
    assignee    TEXT,
    resolved_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_tickets_status ON tickets (status, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_tickets_pc ON tickets (pc_name, created_at DESC);

CREATE TABLE IF NOT EXISTS ticket_messages (
    id         SERIAL PRIMARY KEY,
    ticket_id  INTEGER NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    author     TEXT NOT NULL,
    body       TEXT NOT NULL,
    internal   BOOLEAN NOT NULL DEFAULT FALSE    -- TRUE: yalnızca panelde görünen iç not
);
CREATE INDEX IF NOT EXISTS idx_ticket_messages_ticket ON ticket_messages (ticket_id, id);
