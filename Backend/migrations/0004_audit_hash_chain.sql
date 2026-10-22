-- 0004: device_audit_logs için kurcalanamaz (tamper-evident) hash zinciri.
--
-- Her denetim kaydı bir öncekinin hash'ini (prev_hash) ve kendi hash'ini (entry_hash)
-- taşır: entry_hash = SHA-256(prev_hash | hw_id | action | reason | changes | timestamp).
-- Böylece herhangi bir satır sonradan değiştirilir ya da silinirse zincir kırılır ve
-- /api/system/audit-verify bunu tespit eder. device_audit_logs ajanların YAZAMADIĞI
-- güvenlik denetim tablosudur (kimlik reddi, enroll, güncelleme sonucu buraya düşer).
--
-- NOT (yol haritası): tam kurcalanamazlık için tabloyu uygulama rolünden AYRI bir role
-- ait yapıp uygulamaya yalnızca INSERT/SELECT vermek gerekir; sahibi olan rol her zaman
-- UPDATE/DELETE edebildiği için tek-rol kurulumda hash zinciri "tespit", "engelleme" değildir.
-- Bu dosya idempotenttir.

ALTER TABLE device_audit_logs ADD COLUMN IF NOT EXISTS prev_hash  TEXT;
ALTER TABLE device_audit_logs ADD COLUMN IF NOT EXISTS entry_hash TEXT;
