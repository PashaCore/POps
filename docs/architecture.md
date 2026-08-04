# POps — Pasha Operations Platform

**POps**, okul laboratuvarları ve kurumsal ortamlar için geliştirilmiş, uçtan uca bir **Endpoint Yönetim ve İzleme Platformu**dur. Uzak masaüstü denetimi, ajan tabanlı gerçek zamanlı telemetri, politika yönetimi, yazılım dağıtımı ve güvenlik izolasyonu gibi işlevleri tek bir çatı altında birleştirir.

---

## Bakımı Yapan

Bu platform, Pasha tarafından okul bilgisayar laboratuvarlarında kurum içi BT yönetimini kolaylaştırmak amacıyla tasarlanıp geliştirilmektedir.

---

## Proje Yapısı

`
Yama/
├── Agent-Architecture/         # İstemci tarafı .NET ajan bileşenleri (C#)
│   ├── POpsAgent/              # Ana arka plan servisi (Windows Service)
│   ├── POpsTray/               # Kullanıcı bildirimi ve etkileşim (System Tray)
│   ├── POpsVision/             # Gerçek zamanlı ekran görüntüsü tüneli
│   ├── POpsWatchdog/           # Ajan sürekliliğini koruyan koruyucu süreç
│   ├── POpsUpdater/            # Otomatik güncelleme istemcisi
│   └── Install-POpsAgent.ps1   # Uzak kurulum scripti (PowerShell)
│
├── Back-End/
│   └── server.py               # FastAPI + PostgreSQL tabanlı ana API sunucusu
│
└── Front-End/                  # PHP tabanlı web yönetim paneli
    ├── index.php               # Ana gösterge paneli (Dashboard)
    ├── devices.php             # Cihaz listesi ve anlık durum
    ├── labs.php                # Laboratuvar görünümü ve yönetimi
    ├── vision.php              # Canlı ekran izleme (Vision)
    ├── terminal.php            # Uzak terminal
    ├── logger.php              # Olay günlüğü (Log Viewer)
    ├── tasks.php               # Görev kuyruğu
    ├── deploy.php              # Yazılım paketi dağıtımı
    ├── update.php              # Ajan güncelleme yönetimi
    ├── policies.php            # Güvenlik politikaları
    ├── settings.php            # Sistem ayarları
    └── login.php               # Giriş sayfası
`

---

## Ajan Mimarisi

Her uç noktaya 5 bileşen kurulur. Bunlar birlikte **Dual-Socket Mimarisi** ile çalışır.

### 1. POpsAgent — Ana Arka Plan Servisi

- .NET 8 BackgroundService; Windows Servisi olarak yüklenir.
- Sunucuya **kalıcı WebSocket tüneli** kurar (/ws/agent/{hw_id}).
- Her 5 saniyede bir **kalp atışı (heartbeat)** gönderir: CPU, RAM, disk ve aktif pencere bilgisi.
- Benzersiz **Donanım Kimliği (HWID)** hesaplar: SHA256(MachineName + DiskSerial + MacAddress + BIOS_UUID). C:\POpsData\identity.key dosyasında saklanır.
- **AdvancedActivityTracker** modülü ile etkinlik izler:
  - DNS/Ağ İzleme: Aktif TCP bağlantılarını tarar; politikaya göre yasak kategorileri (kumar, yetişkin, zararlı yazılım) tespit eder.
  - Süreç İzleme (WMI): Başlatılan uygulamaları Win32_ProcessStartTrace ile yakalar.
  - USB Takip: Cihaz takma/çıkarma olaylarını Win32_DeviceChangeEvent ile loglar.
  - Dosya Sistemi: Masaüstü, İndirmeler, Belgeler klasörlerindeki değişiklikleri izler.
  - Boşta Kalma: 5 dakikadan fazla hareketsizliği tespit eder.
  - Aktif Pencere: Önde açık uygulamanın başlığını raporlar.
- Sunucudan gelen komutları işler:
  - execute — Uzaktan komut çalıştır
  - lockdown / unlock — Kiosk modunu aç/kapat
  - start_stream / stop_stream — Vision tünelini başlat/durdur
  - update_agent — Ajana güncelleme paketi uygula
  - wake_peer — Ağdaki başka cihaza Wake-on-LAN gönder
  - set_identity — HWID'i yeniden ata
  - start_vision_session — Uzaktan görüntü oturumu başlat
- Politika ihlali eşiği aşıldığında 
etsh advfirewall ile otomatik **ağ izolasyonu** (karantina) uygular.
- Her 60 saniyede /api/agent_policies uç noktasını kontrol ederek politika güncellemelerini alır.

### 2. POpsTray — Sistem Tepsisi Ajanı

- Kullanıcının masaüstü oturumunda (Session 1) görünür veya gizli modda çalışır.
- Ana servis ile **Named Pipe** (POpsTrayPipe) üzerinden haberleşir.
- Balon bildirim (BalloonTip) gösterir: bağlantı durumu, karantina bildirimi, uzaktan destek uyarısı.
- **Kilitleme (Kiosk) Modu:** Sunucudan lockdown komutu geldiğinde tam ekran kiosk formu açar; unlock ile kaldırılır.
- **Uzaktan Destek İsteği:** Yönetici bağlantı isteği gönderdiğinde onay penceresi gösterir. Zorunlu oturumlarda geri sayım ekranı açılır.
- **Bypass Tokeni:** Kullanıcı, yöneticinin oluşturduğu 6 haneli tokeni girerek izolasyonu kaldırabilir.
- Sağ tıklama menüsü: İzlemeyi duraklat (15 dk), WatchDog'u duraklat, Hakkında, Yönetici Bypass.
- Adil Kullanım Politikası: Sunucuda tanımlı ise başlangıçta kullanıcıya gösterilir (panelden pasife alınabilir).

### 3. POpsVision — Ekran Görüntüsü Tüneli

- Ekranı JPEG formatında yakalar ve sunucuya WebSocket (/ws/vision/{pc_name}) üzerinden iletir.
- Web panelinde canlı görüntüleme ve uzaktan giriş (fare + klavye) destekler.
- FPS oranı (1-5) sunucu tarafından dinamik olarak ayarlanabilir.
- Grid (çoklu) ve tekil cihaz görüntüleme modları.

### 4. POpsWatchdog — Koruyucu Süreç

- schtasks aracılığıyla kullanıcı oturumunda (Session 1) çalıştırılır.
- POpsAgent servisinin çalışıp çalışmadığını periyodik olarak kontrol eder; düşmüşse otomatik yeniden başlatır.
- C:\POpsData\watchdog_pause.flag dosyası varsa duraklama moduna girer.

### 5. POpsUpdater — Otomatik Güncelleme

- Sunucudan en son ajan sürümünü (/api/latest_update) kontrol eder.
- Yeni sürüm varsa paketi indirir, servisi durdurur, dosyaları günceller ve servisi yeniden başlatır.

---

## Back-End — FastAPI API Sunucusu

server.py: Python + FastAPI, PostgreSQL veritabanı, WebSocket desteği.

### Kimlik Doğrulama

| Uç Nokta | Yöntem | Açıklama |
|---|---|---|
| /api/auth/login | POST | Giriş (JWT token üret) |
| /api/auth/failed | POST | Başarısız giriş logu |
| /api/auth/logout | POST | Çıkış |
| /api/admin/users | GET/POST | Admin kullanıcıları listele/ekle |
| /api/admin/users/{id} | PUT/DELETE | Admin kullanıcıyı düzenle/sil |

### Cihaz Yönetimi

| Uç Nokta | Yöntem | Açıklama |
|---|---|---|
| /api/devices | GET | Tüm cihazları listele |
| /api/devices/{pc_name} | DELETE | Cihazı sil |
| /api/inventory/{pc_name} | POST | Ajan donanım envanteri gönder |
| /api/inventory | GET | Tüm envanterleri getir |

### Laboratuvar Yönetimi

| Uç Nokta | Yöntem | Açıklama |
|---|---|---|
| /api/create_lab | POST | Yeni lab oluştur |
| /api/custom_labs | GET | Tüm labları listele |
| /api/rename_lab | POST | Lab adını değiştir |
| /api/delete_lab | POST | Lab sil |
| /api/move_pc | POST | Cihazı laba taşı |
| /api/move_pcs | POST | Birden fazla cihazı laba taşı |
| /api/set_main_pc | POST | Ana PC'yi belirle |
| /api/save_lab_layout | POST | Lab düzenini kaydet |
| /api/lab_settings | GET | Lab ayarlarını getir |
| /api/set_auto_enroll | POST | Otomatik kayıt aç/kapat |
| /api/set_concurrent_limit | POST | Es zamanlı erisim limitini ayarla |
| /api/get_concurrent_limit | GET | Limiti getir |

### Güvenlik ve Izolasyon

| Uç Nokta | Yöntem | Açıklama |
|---|---|---|
| /api/security/lockdown | POST | Cihazı kilitle (Kiosk) |
| /api/security/unlock | POST | Kilidi kaldir |
| /api/security/bypass_token/{pc_name} | GET | 6 haneli bypass tokeni olustur |
| /api/agent_policies | GET/POST | Politika oku/güncelle |
| /api/policy_alert | POST | Politika ihlali bildirimi (Ajan→Sunucu) |

### Görevler ve Kontrol

| Uç Nokta | Yöntem | Açıklama |
|---|---|---|
| /api/tasks | GET | Görev kuyruğunu getir |
| /api/tasks/action | POST | Göreve aksiyon (onayla/reddet/iptal) |
| /api/flush_queue | POST | Kuyruğu temizle |
| /api/wake_pc/{pc_name} | POST | Wake-on-LAN gönder |
| /api/wake_lab/{lab_name} | POST | Tüm laba WoL gönder |
| /api/wake_all | POST | Tüm cihazlara WoL gönder |

### Yazilim ve Güncelleme Dagitimi

| Uç Nokta | Yöntem | Açıklama |
|---|---|---|
| /api/upload | POST | Dagitim paketi yükle |
| /api/packages | GET | Paketleri listele |
| /api/add_package | POST | Paket ekle |
| /api/delete_package | POST | Paket sil |
| /api/storage | GET | Depolama kullanimi |
| /api/deploy_orchestration | POST | Toplu/secici dagitim baslat |
| /api/upload_update | POST | Ajan güncelleme paketi yükle |
| /api/latest_update | GET | Son güncelleme bilgisini getir |
| /api/update_agent/{hw_id} | POST | Belirli ajana güncelleme gönder |
| /api/broadcast_update | GET | Tüm ajanlara güncelleme gönder |
| /api/agent_versions | GET | Ajan sürümlerini listele |
| /api/updates | GET | Güncelleme dosyalarini listele |
| /api/updates/{filename} | DELETE | Güncelleme dosyasini sil |

### Vision (Uzak Masaüstü)

| Uç Nokta | Yöntem | Açıklama |
|---|---|---|
| /api/stream/start/{pc_name} | GET | Ekran yayinini baslat |
| /api/stream/stop/{pc_name} | GET | Ekran yayinini durdur |
| /api/thumbnail/{pc_name} | GET | Anlik ekran görüntüsü |
| /api/remote_input | POST | Uzak fare/klavye girdisi gönder |

### Loglama ve Denetim

| Uç Nokta | Yöntem | Açıklama |
|---|---|---|
| /api/logs/{pc_name} | POST | Ajan log gönder |
| /api/logs | GET | Tüm loglari getir |
| /api/audit/session/start | POST | Denetim oturumu baslat |
| /api/audit/session/end | POST | Denetim oturumunu bitir |

### WebSocket Kanallari

| Kanal | Açıklama |
|---|---|
| /ws/panel | Panel → Tüm ajan komut yayini (broadcast) |
| /ws/agent/{pc_name} | Ajan ↔ Sunucu cift yönlü komut kanali |
| /ws/vision/{pc_name} | Ajan ↔ Panel ekran görüntüsü tüneli |

---

## Front-End — Web Yönetim Paneli

PHP tabanlı, JWT ile korunan yönetim arayüzü.

| Sayfa | Açıklama |
|---|---|
| Dashboard (index.php) | Anlik cihaz sayisi, aktif baglantilar, CPU/RAM ortalamasi, son olaylar, radar haritasi |
| Cihazlar (devices.php) | Tüm cihazlari listele, durum göster, filtrele |
| Laboratuvarlar (labs.php) | Lab olustur/yeniden adlandir/sil, cihazlari sürükle-birak ile tasI, WoL gönder, limit belirle |
| Vision (vision.php) | Canli ekran izleme, cihazdan cihaza geçis, uzaktan fare ve klavye kontrolü, thumbnail grid |
| Terminal (terminal.php) | Secili cihaza uzaktan komut gönder, terminal çiktisini gerçek zamanli göster |
| Logger (logger.php) | Tüm ajanlardan gelen olaylari filtrele (cihaz, risk, kategori, tarih), detay görüntüle |
| Görevler (tasks.php) | Bekleyen görev kuyrugu, onayla/reddet/iptal et |
| Dagitim (deploy.php) | ZIP tabanli kurulum paketi yükle, hedef cihaz/lab sec, dagitim orkestrasyonu baslat |
| Güncelleme (update.php) | Ajan güncelleme paketi yükle, sürüm karsilastir, tekil/toplu güncelleme baslat |
| Politikalar (policies.php) | Adil kullanim metni, DNS filtre kategorileri, otomatik karantina esigi |
| Ayarlar (settings.php) | API URL, WebSocket URL, sistem geneli yapilandirma |
| Giris (login.php) | JWT tabanli yönetici girisi |

---

## Güvenlik Modeli

POps'un güvenlik yaklasimi ag katmani tabanlidir — keylogger içermez, tus kaydetmez.

- **HWID:** SHA256(MachineName + DiskSerial + MacAddress + BIOS_UUID). C:\POpsData\identity.key dosyasinda saklanir.
- **Karantina:** netsh advfirewall ile tüm gelen/giden trafik engellenir; yalnizca POps sunucu adresi açik kalir.
- **Bypass Tokeni:** 6 haneli TOTP benzeri token; panel üzerinden üretilir, ajan üzerinden girilir.
- **Kiosk Modu:** Tam ekran kilit formu; kullanicinin sisteme erişimi engellenir.
- **DNS Filtresi:** Yasakli kategori listesi her dakika güncellenir; ihlal sayisi esigi astiginda otomatik karantina.
- **JWT:** Web paneline erisim JWT token ile korunur.
- **Denetim Kayitlari:** Tüm admin eylemleri agent_logs_v2 tablosuna kaydedilir.

---

## Log Semasi (agent_logs_v2)

| Sütun | Tip | Açıklama |
|---|---|---|
| id | SERIAL | Birincil anahtar |
| pc_name | TEXT | Cihaz adi |
| actor_id | TEXT | Aktör kimliği (Admin adi, System vb.) |
| event_type | TEXT | Olay tipi (agent.kiosk.lockdown, dns.violation vb.) |
| category | TEXT | Kategori (security, system, network, file) |
| action | TEXT | Gerceklestirilen eylem |
| risk_level | TEXT | Risk seviyesi: info, medium, high, critical |
| reason | TEXT | Neden / Gerekce |
| message | TEXT | Insan okunabilir mesaj |
| meta_data | JSONB | Ek veri (JSON) |
| timestamp | TIMESTAMPTZ | Olay zamani |

---

## Kurulum

### Gereksinimler

**Sunucu:**
- Python 3.11+
- PostgreSQL 14+
- FastAPI, Uvicorn, asyncpg, python-jose

**Ajan (Her uc nokta):**
- Windows 10/11 (x64)
- .NET 8 Runtime
- Yönetici (Administrator) yetkisi

### Sunucu Kurulumu

`ash
cd Yama/Back-End
python -m venv venv
venv\Scripts\activate
pip install -r requirements.txt
createdb pashacore_db
uvicorn server:app --host 0.0.0.0 --port 8000
`

Systemd servis dosyasi (/etc/systemd/system/pops.service):

`ini
[Unit]
Description=POps API Server
After=network.target postgresql.service

[Service]
User=www-data
WorkingDirectory=/opt/PashaCore_API
ExecStart=/opt/PashaCore_API/venv/bin/uvicorn server:app --host 127.0.0.1 --port 8000
Restart=always

[Install]
WantedBy=multi-user.target
`

### Ajan Kurulumu

1. Projeleri derle:
`powershell
dotnet publish POpsTray\POpsTray.csproj -c Release -r win-x64 --self-contained false
dotnet publish POpsAgent\POpsAgent.csproj -c Release -r win-x64 --self-contained false
`

2. Kurulum scriptini Yönetici olarak calistir:
`powershell
powershell -ExecutionPolicy Bypass -File Install-POpsAgent.ps1
`

3. Yapilandirma dosyasi (C:\POpsData\config.json):
`json
{
   server_url: https://dev.pashacore.com.tr
}
`

### Web Paneli

Front-End/ klasörünü Apache/Nginx altinda PHP 8.1+ ile sun. includes/config.php dosyasindaki API URL'sini güncelle.

---

## Veri Akisi

`
[Windows Uc Nokta]
  POpsAgent (Windows Service)
    |-- WebSocket --> /ws/agent/{hwid}       --> [FastAPI Sunucu]
    |-- WebSocket <-- /ws/agent/{hwid}       <-- [Komut Alma]
    |-- Named Pipe --> POpsTray              [Bildirim/Kiosk/Bypass]
    |-- Named Pipe --> POpsVision            [Ekran Görüntüsü]
    +-- WebSocket --> /ws/vision/{pcname}    --> [FastAPI Sunucu]
                                                     |
                                                     v
                                           [PostgreSQL Veritabani]
                                                     |
                                                     v
                                           [Web Paneli (PHP/JS)]
                                    Dashboard / Vision / Terminal /
                                    Logger / Deploy / Politika
`

---

## Yol Haritasi

- [ ] Coklu Kiracı Desteği — Birden fazla kurum/okul icin izole ortamlar
- [ ] Mobil Panel — iOS/Android yönetim uygulamasi
- [ ] Raporlama — Haftalik/aylik etkinlik ve uyumluluk raporu (PDF)
- [ ] Active Directory Entegrasyonu — Kullanici bazli politika atamasi
- [ ] Paket Deposu — Merkezi yazilim deposu ve lisans yönetimi
- [ ] Depolama Kotasi — Lab bazli kota yönetimi (hedef: 20GB/lab)
- [ ] Gercek Zamanli DNS Filtresi — hosts dosyasi veya yerel DNS proxy

---

**POps — Pasha Operations Platform**
Copyright 2026. Tüm hakları saklıdır.
