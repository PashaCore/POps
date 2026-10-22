-- 0002: ajan kaydı (enroll) jetonları + kalıcı per-ajan secret (Faz 3 kimlik doğrulama)
--
-- /ws/agent ve kimliksiz ajan HTTP uçlarındaki impersonation açığını kapatmanın veri modeli.
--
--   * enroll_tokens: kurulumda verilen TEK KULLANIMLIK, laba bağlı, SÜRELİ kayıt jetonu
--     (bypass_tokens deseniyle aynı: TIMESTAMPTZ, is_used). Ajan ilk bağlanışta bunu sunar;
--     sunucu doğrular, tüketir ve ajana kalıcı bir secret verir.
--   * agent_secrets: kayıt olan ajanın kalıcı secret'ının SHA-256 HASH'i (düz metin SAKLANMAZ).
--     Anahtar clients.pc_name = sunucu-çözümlü DNA kimliğidir (URL yol id'si değil), böylece
--     reconcile_device kimliği değiştirdiğinde secret uygulama koduyla taşınabilir. Donmuş
--     diskte secret silinse bile ajan, DNA ile tanınan cihaz olarak kanaldan yeni secret alabilir
--     (freeze-safe; tek kullanımlık jeton tekrar gerekmez).
--
-- Şema kasıtlı olarak FK'sızdır; temizlik uygulama kodunda yapılır (delete_device).
-- Bu dosya idempotenttir: mevcut bir veritabanında tekrar çalıştırılması bir şey değiştirmez.

CREATE TABLE IF NOT EXISTS enroll_tokens (
    id         SERIAL PRIMARY KEY,
    token      TEXT NOT NULL UNIQUE,
    lab_name   TEXT,                       -- kayıt olan cihaz bu laba düşer (NULL = Atanmamis_Cihazlar)
    note       TEXT,                       -- panelde görünen açıklama (ör. "Lab-3 kurulumu")
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    is_used    BOOLEAN NOT NULL DEFAULT FALSE,
    used_by    TEXT,                       -- enroll eden HW- kimliği
    used_at    TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_enroll_tokens_token ON enroll_tokens (token);

CREATE TABLE IF NOT EXISTS agent_secrets (
    pc_name     TEXT PRIMARY KEY,          -- sunucu-çözümlü HW- kimliği (clients.pc_name)
    secret_hash TEXT NOT NULL,             -- ajan secret'ının SHA-256'sı (düz metin saklanmaz)
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    rotated_at  TIMESTAMPTZ
);
