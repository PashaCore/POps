-- 0011: karantina komutlarının teslimi. Yönetici kilit/kilit açma verdiğinde cihaz çevrimdışı olabilir ya da
-- komut ulaşmayabilir; istenen durum burada saklanır ve ajan heartbeat'te kendi durumunu bildirince eşitlenir.
ALTER TABLE clients ADD COLUMN IF NOT EXISTS pending_quarantine_action TEXT;   -- lock | unlock | NULL
ALTER TABLE clients ADD COLUMN IF NOT EXISTS pending_quarantine_reason TEXT;
