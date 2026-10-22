-- 0033: dosya aktarımı için kalıcı kapatma isteği (uzak komut ve uzak ekrandaki gibi, bkz. 0006).
--
-- Yönetici bir bilgisayarda dosya aktarımını kapatınca istek burada kalır: ajan çevrimdışıysa ya da yeniden kurulup
-- açık bildirirse sunucu set_capabilities ile kapatmayı yeniden gönderir. Sunucu yalnızca kapatabilir; açmak ajanın
-- yeniden kurulmasını ister (FILES_ENABLED=1). Bu dosya idempotenttir.

ALTER TABLE clients ADD COLUMN IF NOT EXISTS cap_files_disable_requested BOOLEAN NOT NULL DEFAULT FALSE;
