-- 0026: cihazın işletim sistemi ailesi (ajanın X-Agent-Platform başlığı; Linux ajanı "linux" gönderir).
--   windows | linux
--   NULL: ajan bildirmiyor (Windows ajanı başlığı göndermez); panel ve API bunu "windows" sayar.
-- Sunucu, bağlanan ajanın değerini her bağlantıda yazar (bkz. routers/agents.py). Ajan güncellemesi her cihaza kendi
-- paketini (MSI ya da .deb) seçtirir; paketi olmayan platformdaki cihazlar atlanır (bkz. system_routes.py).
ALTER TABLE clients ADD COLUMN IF NOT EXISTS platform TEXT;
