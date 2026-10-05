-- 0030: panel girişi için dizin (LDAP / Active Directory) ve OpenID Connect (bkz. pops/sso.py, D-24).
--
-- users.auth_source: hesabın kimliğini kim doğrular. 'local' (varsayılan, mevcut bütün hesaplar) bcrypt şifresiyle
-- girer; 'ldap' ve 'oidc' hesaplarının yerel şifresi yoktur (password_hash '!sso', bcrypt değildir, hiçbir şifre
-- eşleşmez). external_id dizindeki ya da sağlayıcıdaki değişmez kimliktir (LDAP: entryUUID / objectGUID ya da DN;
-- OIDC: "iss|sub"); ilk girişte yazılır ve hesabı o kişiye bağlar.
ALTER TABLE users ADD COLUMN IF NOT EXISTS auth_source TEXT NOT NULL DEFAULT 'local'
    CHECK (auth_source IN ('local', 'ldap', 'oidc'));
ALTER TABLE users ADD COLUMN IF NOT EXISTS external_id TEXT;
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_external_id ON users (auth_source, external_id) WHERE external_id IS NOT NULL;

-- Sağlayıcı ayarları: kind başına bir satır. config gizli olmayan alanlardır (JSON); secret LDAP hizmet hesabının
-- şifresi ya da OIDC istemci sırrıdır, 2FA anahtarlarıyla aynı anahtarla şifreli durur ("v1:", pops/secretbox.py).
CREATE TABLE IF NOT EXISTS sso_providers (
    kind TEXT PRIMARY KEY CHECK (kind IN ('ldap', 'oidc')),
    enabled BOOLEAN NOT NULL DEFAULT FALSE,
    config JSONB NOT NULL DEFAULT '{}'::jsonb,
    secret TEXT,
    updated_by TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Kısa ömürlü OIDC akışları ('oidc_state', 10 dk) ve giriş biletleri ('ticket', 60 sn). Anahtar değerin SHA-256
-- özetidir; değerin kendisi yalnızca tarayıcıda (çerez, adres) durur. Okunan satır silinir: her biri bir kez geçer.
CREATE TABLE IF NOT EXISTS sso_flows (
    id_hash TEXT PRIMARY KEY,
    kind TEXT NOT NULL CHECK (kind IN ('oidc_state', 'ticket')),
    data JSONB NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sso_flows_expires ON sso_flows (expires_at);
