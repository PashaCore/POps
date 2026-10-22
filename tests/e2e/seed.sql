-- Uçtan uca testlerin örnek verisi (yalnızca geçici test veritabanı; db.py seed). Bütün adlar, adresler ve kişiler
-- uydurmadır. Sunucu açıldıktan SONRA yüklenir: açılışta bütün cihazlar "Offline" yazılır, buradaki durumlar kalsın.
-- İki kayıt HTML'e benzer metin taşır (cihaz adı ve olay mesajı): panel bunları kaçırmalı, test hiçbir pencere
-- (alert) açılmadığını denetler.
CREATE OR REPLACE FUNCTION pg_temp.ts(i interval) RETURNS timestamptz LANGUAGE sql AS
$$ SELECT now() - i $$;

INSERT INTO custom_labs (lab_name) VALUES ('Lab-1 Yazılım'), ('Lab-2 Donanım'), ('Kütüphane'), ('Boş Sınıf') ON CONFLICT DO NOTHING;

INSERT INTO clients (pc_name, hostname, lab_name, last_seen, status, active_window, ip_address, display_name, is_quarantined,
                     cap_terminal_enabled, cap_vision_enabled, running_version, cap_server_ca, last_disconnect_at, last_disconnect_reason, agent_health)
VALUES
 ('HW-E2E0000101', 'LAB1-OGR',  'Lab-1 Yazılım', pg_temp.ts('10 seconds'), 'Online', 'Visual Studio Code', '10.20.1.10', 'Öğretmen PC', false, true, true, '0.1.14-alpha', 'custom', NULL, NULL,
  '{"started_at": 1700000000, "last_policy_sync": 1700000300, "last_inventory_upload": 1700000200, "tray_connected": true, "vision_channel": "idle", "loop_errors_1h": 0}'),
 ('HW-E2E0000102', 'LAB1-PC01', 'Lab-1 Yazılım', pg_temp.ts('12 seconds'), 'Online', 'Tarayıcı', '10.20.1.11', NULL, false, true, true, '0.1.14-alpha', 'custom', NULL, NULL, NULL),
 ('HW-E2E0000103', 'LAB1-PC02', 'Lab-1 Yazılım', pg_temp.ts('20 seconds'), 'Idle', '-', '10.20.1.12', NULL, false, true, false, '0.1.14-alpha', 'system', NULL, NULL, NULL),
 ('HW-E2E0000104', 'LAB1-PC03', 'Lab-1 Yazılım', pg_temp.ts('3 hours'), 'Offline', '-', '10.20.1.13', NULL, false, NULL, NULL, '0.1.12-alpha', NULL, now() - interval '3 hours', '1006 (ağ koptu)', NULL),
 ('HW-E2E0000105', 'LAB1-PC04', 'Lab-1 Yazılım', pg_temp.ts('5 seconds'), 'Online', 'Kelime işlemci', '10.20.1.14', '<img src=x onerror=alert(1)>', false, true, true, '0.1.14-alpha', 'custom', NULL, NULL, NULL),
 ('HW-E2E0000201', 'LAB2-PC01', 'Lab-2 Donanım', pg_temp.ts('8 seconds'), 'Online', 'Dosya gezgini', '10.20.2.11', NULL, false, true, true, '0.1.13-alpha', 'custom', NULL, NULL, NULL),
 ('HW-E2E0000202', 'LAB2-PC02', 'Lab-2 Donanım', pg_temp.ts('2 days'), 'Offline', '-', '10.20.2.12', NULL, true, true, true, '0.1.13-alpha', 'custom', NULL, NULL, NULL),
 ('HW-E2E0000203', 'LAB2-PC03', 'Lab-2 Donanım', pg_temp.ts('30 seconds'), 'Online', 'Çizim', '10.20.2.13', NULL, false, false, true, '0.1.14-alpha', 'custom', NULL, NULL, NULL),
 ('HW-E2E0000301', 'KUTUP-01',  'Kütüphane', pg_temp.ts('1 day'), 'Offline', '-', '10.20.3.11', NULL, false, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
 ('HW-E2E0000302', 'KUTUP-02',  'Kütüphane', pg_temp.ts('15 seconds'), 'Online', 'Tarayıcı', '10.20.3.12', NULL, false, true, true, '0.1.14-alpha', 'custom', NULL, NULL, NULL),
 ('HW-E2E0000901', 'YENI-PC-1', 'Atanmamis_Cihazlar', pg_temp.ts('40 seconds'), 'Online', '-', '10.20.9.21', NULL, false, true, true, '0.1.14-alpha', 'custom', NULL, NULL, NULL),
 ('HW-E2E0000902', 'YENI-PC-2', 'Atanmamis_Cihazlar', pg_temp.ts('4 hours'), 'Offline', '-', '10.20.9.22', NULL, false, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
ON CONFLICT (pc_name) DO NOTHING;

INSERT INTO hw_inventory (pc_name, hostname, cpu, ram, motherboard, gpu, os_version, ip_address, mac_address, disk_info, last_updated)
SELECT pc_name, hostname, 'Örnek İşlemci 6 çekirdek', '16 GB', 'Örnek Anakart B1', 'Tümleşik grafik', 'Windows 11 Pro 23H2', ip_address,
       '02:E2:E0:' || substr(md5(pc_name), 1, 2) || ':' || substr(md5(pc_name), 3, 2) || ':' || substr(md5(pc_name), 5, 2),
       'C: 256 GB SSD (112 GB boş)', pg_temp.ts('1 hour')
FROM clients WHERE pc_name <> 'HW-E2E0000902'
ON CONFLICT (pc_name) DO NOTHING;

INSERT INTO agent_versions (pc_name, version, last_update)
SELECT pc_name, COALESCE(running_version, '0.1.10-alpha'), pg_temp.ts('1 day') FROM clients
ON CONFLICT (pc_name) DO NOTHING;

INSERT INTO lab_settings (lab_name, main_pc, layout_json) VALUES ('Lab-1 Yazılım', 'HW-E2E0000101', '{}') ON CONFLICT DO NOTHING;

INSERT INTO packages (id, name, type, meta, command, icon, color) VALUES
 ('e2e-paket-1', 'Örnek PDF okuyucu', 'package', 'ornek-pdf-3.5.exe | 7.1 MB', 'powershell.exe -EncodedCommand AA==', 'fa-box-open', '#3b82f6'),
 ('e2e-paket-2', 'Örnek arşivleyici', 'package', 'ornek-arsiv-x64.msi | 1.5 MB (Reboot)', 'powershell.exe -EncodedCommand AA==', 'fa-box-open', '#3b82f6'),
 ('e2e-betik-1', 'Geçici dosya temizliği', 'script', 'Sistem Betiği', 'del /q /s %TEMP%\*', 'fa-terminal', '#f59e0b'),
 ('e2e-betik-2', 'Saat eşitle', 'script', 'Sistem Betiği', 'w32tm /resync', 'fa-terminal', '#f59e0b')
ON CONFLICT DO NOTHING;

INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, output, created_by) VALUES
 ('HW-E2E0000102', 'Lab-1 Yazılım', 'ipconfig /flushdns', 'Completed', pg_temp.ts('2 hours'), 'Windows IP Configuration' || chr(10) || 'Successfully flushed the DNS Resolver Cache.', 'admin'),
 ('HW-E2E0000103', 'Lab-1 Yazılım', 'ipconfig /flushdns', 'Failed', pg_temp.ts('2 hours'), 'Erişim reddedildi.', 'admin'),
 ('HW-E2E0000104', 'Lab-1 Yazılım', 'ipconfig /flushdns', 'Pending', pg_temp.ts('2 hours'), NULL, 'admin'),
 ('HW-E2E0000201', 'Lab-2 Donanım', 'gpupdate /force', 'Running', pg_temp.ts('5 minutes'), NULL, 'admin'),
 ('HW-E2E0000202', 'Lab-2 Donanım', 'gpupdate /force', 'Paused', pg_temp.ts('5 minutes'), NULL, 'admin'),
 ('HW-E2E0000203', 'Lab-2 Donanım', 'gpupdate /force', 'Denied', pg_temp.ts('5 minutes'), NULL, 'admin'),
 ('HW-E2E0000302', 'Kütüphane', 'shutdown /r /f /t 5', 'Completed (Rebooted)', pg_temp.ts('1 day'), NULL, 'admin');

INSERT INTO agent_logs_v2 (pc_name, actor_id, event_type, category, action, risk_level, reason, message, meta_data, "timestamp") VALUES
 ('HW-E2E0000102', 'ogrenci12', 'auth.login', 'security', 'login', 'info', '', 'Oturum açıldı: ogrenci12', '{}', pg_temp.ts('50 minutes')),
 ('HW-E2E0000102', 'Agent', 'policy.alert', 'restricted_content', 'unknown', 'high', 'DNS kural ihlali', 'KURAL İHLALİ: ornek-bahis.invalid (yasadisi_bahis)', '{"domain": "ornek-bahis.invalid"}', pg_temp.ts('40 minutes')),
 ('HW-E2E0000103', 'System', 'deploy.execution', 'system_maintenance', 'unknown', 'info', '', 'Görev: ipconfig /flushdns', '{}', pg_temp.ts('2 hours')),
 ('HW-E2E0000202', 'admin', 'security.lockdown', 'security', 'lockdown', 'critical', 'Şüpheli trafik', 'KARANTİNA BAŞLATILDI by admin - Neden: Şüpheli trafik', '{}', pg_temp.ts('1 day')),
 ('HW-E2E0000105', 'Agent', 'auth.failed', 'security', 'login', 'medium', '', 'Hatalı giriş denemesi <script>alert(1)</script>', '{}', pg_temp.ts('10 minutes')),
 ('HW-E2E0000201', 'System/Update', 'agent.update', 'system_maintenance', 'unknown', 'critical', '', 'Ajan güncelleme sorunu: imza doğrulanamadı', '{}', pg_temp.ts('3 hours'));

-- Kayıtlar sayfasının sayfalaması için 120 olay daha (5 bilgisayar, son 3 gün)
INSERT INTO agent_logs_v2 (pc_name, actor_id, event_type, category, action, risk_level, reason, message, meta_data, "timestamp")
SELECT (ARRAY['HW-E2E0000102', 'HW-E2E0000103', 'HW-E2E0000201', 'HW-E2E0000203', 'HW-E2E0000302'])[1 + g % 5],
       'ogrenci' || (g % 7),
       (ARRAY['auth.login', 'auth.logout', 'policy.alert'])[1 + g % 3],
       (ARRAY['security', 'security', 'restricted_content'])[1 + g % 3],
       (ARRAY['login', 'logout', 'unknown'])[1 + g % 3],
       (ARRAY['info', 'info', 'medium'])[1 + g % 3],
       '',
       (ARRAY['Oturum açıldı: ogrenci', 'Oturum kapandı: ogrenci', 'KURAL İHLALİ: ornek-oyun.invalid (oyun) ogrenci'])[1 + g % 3] || (g % 7),
       (CASE WHEN g % 3 = 2 THEN '{"domain": "ornek-oyun.invalid"}' ELSE '{}' END)::jsonb,
       pg_temp.ts(make_interval(mins => g * 35))
FROM generate_series(1, 120) AS g;

INSERT INTO notifications (event, severity, pc_name, title, detail, is_read) VALUES
 ('policy_alert', 'high', 'HW-E2E0000102', 'Kural ihlali', 'ornek-bahis.invalid', false),
 ('update_problem', 'critical', 'HW-E2E0000201', 'Ajan güncellemesi başarısız', 'imza doğrulanamadı', false),
 ('test', 'info', NULL, 'Test bildirimi', NULL, true);

INSERT INTO scheduled_tasks (name, command, target_mode, targets, schedule_type, time_of_day, weekdays, enabled, next_run, created_by) VALUES
 ('Gece temizliği', 'cleanmgr /sagerun:1', 'LAB', '["Lab-1 Yazılım"]', 'weekly', '03:00', '1,2,3,4,5', true, now() + interval '10 hours', 'admin'),
 ('Saat eşitleme', 'w32tm /resync', 'ALL', '[]', 'daily', '07:30', NULL, false, NULL, 'admin');

INSERT INTO tickets (source, pc_name, reporter, category, subject, body, status, priority) VALUES
 ('agent', 'HW-E2E0000102', 'ogrenci12', 'yazici', 'Yazıcı çıktı vermiyor', 'Lab-1 yazıcısı kağıt sıkıştı diyor ama kağıt yok.', 'open', 'high'),
 ('panel', NULL, 'admin', 'ag', 'Kütüphane kablosuz ağı yavaş', 'Öğle arası çok yavaşlıyor.', 'in_progress', 'normal'),
 ('agent', 'HW-E2E0000201', 'ogrenci7', 'yazilim', 'Ofis lisans uyarısı', NULL, 'resolved', 'low');
INSERT INTO ticket_messages (ticket_id, author, body, internal)
SELECT id, 'admin', 'Bakıyorum, 10 dk içinde orada olurum.', false FROM tickets WHERE subject LIKE 'Yazıcı%';

INSERT INTO device_software (pc_name, name, version, publisher) VALUES
 ('HW-E2E0000102', 'Örnek Tarayıcı', '129.0.1', 'Örnek Yazılım A.Ş.'),
 ('HW-E2E0000103', 'Örnek Tarayıcı', '128.0.4', 'Örnek Yazılım A.Ş.'),
 ('HW-E2E0000102', 'Örnek Ofis Paketi 2021', '16.0.14332', 'Örnek Ofis Ltd.'),
 ('HW-E2E0000201', 'Örnek Ofis Paketi 2021', '16.0.14332', 'Örnek Ofis Ltd.'),
 ('HW-E2E0000201', 'Örnek Arşivleyici (x64)', '23.01', 'Örnek Geliştirici');

INSERT INTO device_patch_status (pc_name, pending_count, pending_security, pending_critical, reboot_required, last_search, updates, last_result) VALUES
 ('HW-E2E0000102', 3, 2, 1, false, now() - interval '5 hours', '[{"kb": "KB0000001", "title": "Örnek toplu güncelleştirme"}]', 'ok'),
 ('HW-E2E0000201', 0, 0, 0, true, now() - interval '1 day', '[]', 'ok');

INSERT INTO licenses (name, match_pattern, publisher, seats, license_type, expires_at, notes, created_by) VALUES
 ('Örnek Ofis okul lisansı', 'Örnek Ofis', 'Örnek Ofis Ltd.', 1, 'per_device', (now() + interval '20 days')::date, 'Sözleşme 2026-01', 'admin');
