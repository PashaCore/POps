-- 0029: GLPI'ye dışa aktarım (bkz. pops/glpi.py, docs/integrations/glpi.md). POps tarafındaki eşleşme tablosu: bir
-- POps kaydının GLPI'deki karşılığı ve gönderilen alanların özeti (yalnızca değişen kayıt yeniden gönderilir).
--   computer       pops_key = cihaz kimliği (HW-…)              -> Computer.id
--   software_link  pops_key = cihaz|program|sürüm               -> Item_SoftwareVersion.id (yalnızca POps'un kurduğu)
--   software_set   pops_key = cihaz kimliği                     -> (glpi_id yok) cihazın yazılım listesinin özeti
--   ticket         pops_key = tickets.id                        -> Ticket.id
--   followup       pops_key = ticket_messages.id                -> ITILFollowup.id
-- state: ok | broken (GLPI'de silinmiş ya da çöpte; yeniden oluşturulmaz) | ambiguous (birden çok eşleşme; tahmin
-- yapılmaz). Ayarlar global_settings'te (glpi_*); jetonlar secretbox ile şifreli.
CREATE TABLE IF NOT EXISTS glpi_links (
    kind        TEXT NOT NULL CHECK (kind IN ('computer', 'software_link', 'software_set', 'ticket', 'followup')),
    pops_key    TEXT NOT NULL,
    glpi_id     INTEGER,
    fingerprint TEXT,
    state       TEXT NOT NULL DEFAULT 'ok' CHECK (state IN ('ok', 'broken', 'ambiguous')),
    error       TEXT,
    synced_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (kind, pops_key)
);
CREATE INDEX IF NOT EXISTS idx_glpi_links_problems ON glpi_links (kind, state) WHERE state <> 'ok';
