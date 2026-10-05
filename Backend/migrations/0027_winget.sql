-- 0027: winget ile paket dağıtımı (pops/winget.py, docs/decisions.md D-23).
-- tasks.kind: görevin ajana nasıl gönderileceği. NULL = komut ("execute", script_path çalıştırılır); 'winget' =
-- "winget_install" iletisi, kurulacak paket payload'da ({"id", "version"}). winget görevinin script_path'i ajanın
-- çalıştıracağı komut satırının okunur hâlidir (panelde ve denetim kaydında gösterilir); ajana gönderilmez.
-- agent_versions.features: ajanın bağlanırken X-Agent-Features başlığıyla duyurduğu özellikler (ör. winget). Başlığı
-- göndermeyen eski ajanda NULL kalır; sunucu winget görevini yalnızca "winget" duyuran ajana gönderir.
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS kind TEXT;
ALTER TABLE tasks ADD COLUMN IF NOT EXISTS payload JSONB;
ALTER TABLE agent_versions ADD COLUMN IF NOT EXISTS features TEXT[];
