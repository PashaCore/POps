-- pops: dump-before
-- 0031: zaman damgaları TEXT yerine TIMESTAMPTZ.
--
-- Eski tablolarda zaman, Python sürecinin yerel saatinde 'YYYY-AA-GG SS:DD:ss' metniydi (ör. tasks.created_at); aynı
-- kayıttaki dispatched_at ise TIMESTAMPTZ idi. Metinler burada sunucunun saat diliminde okunur ve gerçek zaman
-- damgasına çevrilir. Saat dilimi: migrate.py'nin oturuma yazdığı pops.tz (POPS_TZ > sürecin yerel saat dilimi >
-- veritabanının TimeZone ayarı; bkz. pops/timeutil.py), yoksa veritabanının TimeZone ayarı.
--
-- Kullanılan dilim global_settings.audit_time_zone'a yazılır ve bir daha değişmez: device_audit_logs hash zinciri
-- zamanı eski metin biçiminde özetler; metin saklanan değerden bu dilimde yeniden üretilir, böylece bu migration'dan
-- önce yazılmış kayıtların özeti de tutar (pops/auditchain.py). Okunamayan (biçimi bozuk) metin NULL olur.
--
-- Sütun türü değişince üzerindeki indeksler (idx_agent_logs_v2_ts, idx_device_audit_*, idx_tasks_pc_created,
-- idx_tasks_created, idx_enterprise_audit_pc) PostgreSQL tarafından yeni türle yeniden kurulur. Tablolar yeniden
-- yazıldığı için büyük bir agent_logs_v2'de bu migration birkaç saniye ile birkaç dakika sürebilir.
--
-- Bilerek TEXT kalanlar: device_software.install_date (ajanın Windows'tan okuduğu ham değer, ör. 20240131),
-- scheduled_tasks.time_of_day ("SS:DD") ve global_settings'teki tarih değerleri.

INSERT INTO global_settings (key, value)
VALUES ('audit_time_zone', coalesce(nullif(current_setting('pops.tz', true), ''), current_setting('TimeZone')))
ON CONFLICT (key) DO NOTHING;

CREATE FUNCTION pg_temp.pops_legacy_ts(v text) RETURNS timestamptz LANGUAGE sql STABLE AS $$
    SELECT CASE
        WHEN v ~ '^\s*\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}(:\d{2}(\.\d+)?)?\s*$'
        THEN btrim(v)::timestamp AT TIME ZONE
             (SELECT value FROM global_settings WHERE key = 'audit_time_zone')
    END
$$;

ALTER TABLE tasks ALTER COLUMN created_at TYPE timestamptz USING pg_temp.pops_legacy_ts(created_at);
ALTER TABLE tasks ALTER COLUMN created_at SET DEFAULT now();
ALTER TABLE agent_logs_v2 ALTER COLUMN "timestamp" TYPE timestamptz USING pg_temp.pops_legacy_ts("timestamp");
ALTER TABLE agent_logs_v2 ALTER COLUMN "timestamp" SET DEFAULT now();
ALTER TABLE agent_logs ALTER COLUMN "timestamp" TYPE timestamptz USING pg_temp.pops_legacy_ts("timestamp");
ALTER TABLE device_audit_logs ALTER COLUMN "timestamp" TYPE timestamptz USING pg_temp.pops_legacy_ts("timestamp");
ALTER TABLE clients ALTER COLUMN last_seen TYPE timestamptz USING pg_temp.pops_legacy_ts(last_seen);
ALTER TABLE hw_inventory ALTER COLUMN last_updated TYPE timestamptz USING pg_temp.pops_legacy_ts(last_updated);
ALTER TABLE agent_versions ALTER COLUMN last_update TYPE timestamptz USING pg_temp.pops_legacy_ts(last_update);
ALTER TABLE users ALTER COLUMN last_login TYPE timestamptz USING pg_temp.pops_legacy_ts(last_login);
ALTER TABLE enterprise_audit_logs ALTER COLUMN start_time TYPE timestamptz USING pg_temp.pops_legacy_ts(start_time);
ALTER TABLE enterprise_audit_logs ALTER COLUMN end_time TYPE timestamptz USING pg_temp.pops_legacy_ts(end_time);

-- Bypass anahtarı zamanları saat dilimsiz TIMESTAMP'ti (DEFAULT NOW(): oturumun TimeZone'unda yerel saat)
ALTER TABLE agent_bypass_keys ALTER COLUMN issued_at TYPE timestamptz USING issued_at AT TIME ZONE current_setting('TimeZone');
ALTER TABLE agent_bypass_keys ALTER COLUMN confirmed_at TYPE timestamptz USING confirmed_at AT TIME ZONE current_setting('TimeZone');

DROP FUNCTION pg_temp.pops_legacy_ts(text);
