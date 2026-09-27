-- 0008: JWT iptali (token_version) + görev sahipliği (tasks.created_by) — pentest F4.
--
-- token_version: JWT içine gömülür; verify_session her istekte DB'deki değerle karşılaştırır.
-- Kullanıcı silinince (satır yok), rolü düşürülünce ya da şifresi değişince sunucu bu sayacı
-- artırır → o kullanıcının ELDEKI tüm eski jetonları anında geçersiz olur (12 saat beklemeden).
-- Eski (tv iddiası olmayan) jetonlar tv=0 sayılır; DB varsayılanı 0 olduğundan mevcut oturumlar
-- deploy'da düşmez, ilk role/şifre değişiminde geçersiz olur.
--
-- tasks.created_by: SYSTEM olarak komut çalıştıran görevi KİMİN kuyrukladığını kaydeder
-- (eskiden 'System/Queue' idi, iz yoktu). Bu dosya idempotenttir.

ALTER TABLE users ADD COLUMN IF NOT EXISTS token_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS created_by TEXT;
