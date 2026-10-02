-- 0018: modüller (bkz. pops/modules.py, docs/design/modules.md). Bir modülün kurum geneli ('org', scope_id '')
-- ya da laboratuvar bazında ('lab', scope_id = lab adı) açık/kapalı ayarı. Satır yoksa modül açıktır; en özel
-- ayar kazanır. config, modüle özel ayarlar içindir (gizli değerler secretbox ile şifrelenir).
CREATE TABLE IF NOT EXISTS module_settings (
    module_id  TEXT NOT NULL,
    scope_type TEXT NOT NULL CHECK (scope_type IN ('org', 'lab')),
    scope_id   TEXT NOT NULL DEFAULT '',
    enabled    BOOLEAN,
    config     JSONB NOT NULL DEFAULT '{}',
    updated_by TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (module_id, scope_type, scope_id)
);

-- Kurulum profili: mevcut kurulumlar (cihazı olan) "custom" başlar ve hiçbir modül kapanmaz. Yeni kurulumda
-- ayar yoktur; panel ilk açılışta superadmin'e profil sorar.
INSERT INTO global_settings (key, value)
SELECT 'install_profile', 'custom' WHERE EXISTS (SELECT 1 FROM clients)
ON CONFLICT (key) DO NOTHING;
