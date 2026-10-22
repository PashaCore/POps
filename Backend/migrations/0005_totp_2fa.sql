-- 0005: Panel yöneticileri için opt-in TOTP (RFC 6238) iki adımlı doğrulama.
--
-- İki sütun eklenir:
--   totp_secret  — base32 gizli anahtar (kullanıcı kaydı başlatınca yazılır; onaylanana
--                  kadar totp_enabled=false kalır, yani henüz zorunlu değildir).
--   totp_enabled — kullanıcı bir doğrulama kodunu ONAYLADIKTAN sonra true olur; ancak o
--                  zaman girişte kod istenir. Böylece kimse yanlış kurulumla kilitlenmez.
--
-- Varsayılan KAPALI: mevcut hesaplar (totp_enabled=false) eskisi gibi yalnızca şifreyle
-- girer. Kilitlenme kurtarma yolu (authenticator kaybı): sunucuda psql ile
--   UPDATE users SET totp_enabled=false, totp_secret=NULL WHERE username='<kullanıcı>';
-- Bu dosya idempotenttir.

ALTER TABLE users ADD COLUMN IF NOT EXISTS totp_secret  TEXT;
ALTER TABLE users ADD COLUMN IF NOT EXISTS totp_enabled BOOLEAN NOT NULL DEFAULT FALSE;
