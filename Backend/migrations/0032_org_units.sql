-- 0032: kurum birimleri (ilçe → okul) ve kapsamlı yetki (bkz. pops/tenancy.py, docs/decisions.md D-25).
--
-- Mevcut kurulumlarda hiçbir şey değişmez: birim yoktur, bütün kullanıcı ve jetonların kapsamı NULL'dır (her şeyi
-- görür). Süper admin birim kurup laboratuvarları bağlayınca ve kullanıcılara kapsam verince geçerli olur.

CREATE TABLE IF NOT EXISTS org_units (
    id         SERIAL PRIMARY KEY,
    name       TEXT NOT NULL,
    parent_id  INTEGER REFERENCES org_units(id),   -- ilçe -> okul; NULL = en üst birim
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
-- Aynı üst birimin altında aynı ad bir kez (büyük/küçük harf duyarsız)
CREATE UNIQUE INDEX IF NOT EXISTS idx_org_units_name ON org_units (coalesce(parent_id, 0), lower(name));
CREATE INDEX IF NOT EXISTS idx_org_units_parent ON org_units (parent_id);

-- Laboratuvarın birimi (NULL: hiçbir birime bağlı değil, yalnızca kapsamsız hesaplar görür)
ALTER TABLE custom_labs ADD COLUMN IF NOT EXISTS org_unit_id INTEGER REFERENCES org_units(id) ON DELETE SET NULL;
CREATE INDEX IF NOT EXISTS idx_custom_labs_unit ON custom_labs (org_unit_id) WHERE org_unit_id IS NOT NULL;

-- Kapsam: NULL = her şey; birim kimlikleri = o birimler ve alt birimleri. Birim silinince listeden çıkarılır.
ALTER TABLE users ADD COLUMN IF NOT EXISTS org_scope INTEGER[];
ALTER TABLE api_tokens ADD COLUMN IF NOT EXISTS org_scope INTEGER[];
-- Zamanlanmış görev, oluşturanın kapsamını taşır: hedefleri her çalışmada bu kapsamla çözülür
ALTER TABLE scheduled_tasks ADD COLUMN IF NOT EXISTS org_scope INTEGER[];
-- Lisansın ve (cihazsız) destek talebinin birimi; NULL = kurum geneli
ALTER TABLE licenses ADD COLUMN IF NOT EXISTS org_unit_id INTEGER REFERENCES org_units(id) ON DELETE SET NULL;
ALTER TABLE tickets ADD COLUMN IF NOT EXISTS org_unit_id INTEGER REFERENCES org_units(id) ON DELETE SET NULL;

-- Kapsam süzgeci cihazları laboratuvara göre arar
CREATE INDEX IF NOT EXISTS idx_clients_lab ON clients (lab_name);
