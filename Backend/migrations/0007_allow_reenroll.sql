-- 0007: Enroll token ile mevcut cihazın secret'ının ELE GEÇİRİLMESİNİ engelle (pentest F2).
--
-- Sorun: /ws/agent enroll bloğu agent_secrets satırını ON CONFLICT (pc_name) DO UPDATE ile
-- koşulsuz eziyordu. Geçerli (çok-kullanımlık) bir enroll token + hedef cihazın DNA'sını
-- (uuid+bios) sunan saldırgan, o cihazın secret'ını kendi ürettiği yenisiyle değiştirip
-- kimliğini çalabiliyordu. Bundan sonra sunucu: zaten secret'ı OLAN bir cihaza düz enroll
-- token'la yeniden-secret vermeyi REDDEDER (Critical audit + 4401), MEĞER Kİ yönetici o cihaz
-- için allow_reenroll'u açmış olsun (Deep Freeze kurtarma / yeniden kurulum gibi meşru durum).
-- Bayrak tek-seferliktir: başarılı yeniden-enroll'da sunucu otomatik FALSE'a çeker.
-- Bu dosya idempotenttir.

ALTER TABLE clients ADD COLUMN IF NOT EXISTS allow_reenroll BOOLEAN NOT NULL DEFAULT FALSE;
