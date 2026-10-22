-- 0022: API jetonları (otomasyon: betikler, izleme, dış sistemler; panel girişinin JWT'sinden ayrı). Jetonun kendisi
-- saklanmaz: token_hash SHA-256 özetidir, token_prefix "pops_" sonrasındaki ilk 8 karakterdir (listede tanımak için).
-- Rol viewer ya da admin olabilir, superadmin olamaz. İptal edilen jeton silinmez (revoked_at); ad bütün jetonlar
-- arasında tektir, çünkü görev ve denetim kayıtlarına "token:<ad>" olarak yazılır. Bkz. pops/security.py, D-21.
CREATE TABLE IF NOT EXISTS api_tokens (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    token_hash TEXT NOT NULL UNIQUE,
    token_prefix TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('viewer', 'admin')),
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ,
    revoked_at TIMESTAMPTZ
);
