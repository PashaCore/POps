-- 0020: ajanın çalıştırmadığı komutlar (uzak komut kapalı, modül kapalı) eski sunucuda "Completed" kalmış olabilir:
-- ajan ret sonucunu çıkış kodsuz "[REDDEDİLDİ] …" metniyle gönderiyordu. Bunlar "Denied" (çıkış kodu -5) olur; sunucu
-- artık yeni sonuçları da böyle yazar.
UPDATE tasks SET status = 'Denied', exit_code = -5
WHERE status = 'Completed' AND exit_code IS NULL AND output LIKE '[REDDEDİLDİ]%';
