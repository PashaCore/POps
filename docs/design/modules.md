# Tasarım taslağı: modüller ve kurulum profilleri

> **Durum: kararlar verildi (bölüm 10), uygulama başlıyor.** Kod yazılmadı. Kararlar netleşince ana noktaları `docs/decisions.md`'ye
> (İngilizce) geçer, bu dosya uygulama rehberi olarak kalır.

## 1. Amaç

Tek kurulum, tek panel; özellikler kurumun ihtiyacına göre açılıp kapanır. Bir okul laboratuvarı kurulumu ile bir
kurumun genel bilgisayar yönetimi aynı yazılımdır, yalnızca açık modüller ve varsayılanlar farklıdır.

Bir modül kapatıldığında bu yalnızca menüden gizlenmek değildir: panel, sunucu ve ajan aynı kararı uygular. Açık
oturumlar kapanır, ilgili işler durur.

Bu belge önce **bugün var olan özellikleri** modül yapısına oturtur. Sonra üç yeni modülün (kurumsal giriş, uygulama
kataloğu, SIEM'e kayıt gönderme) bu yapıya nasıl gireceğini gösterir. İmaj dağıtımı (FOG) ve öğretmen modu (Veyon
ya da kendi ekranımız) sonraki karar; yapı onları da taşıyacak şekilde kurulur.

## 2. Bugünkü durum ve kısıtlar

- **Ajan tarafı yetenekler:** Terminal ve Vision bilgisayarda kurulumda kapatılabilir
  (`C:\POpsData\secure\capabilities.json`). Sunucu yalnızca kapatabilir, açamaz. Bu "yerel sert kilit" olarak
  aynen kalır.
- **Panel izinleri:** Kullanıcı başına sayfa listesi (`users.permissions`). Yalnızca menüyü belirler; API rolden
  bakar (bilinen sınır, `docs/security.md`).
- **Önemli kısıt:** Yazılım dağıtımı, zamanlanmış görevler ve terminal hızlı butonları ajanda aynı `execute`
  yolundan gider. Terminali kapalı bir bilgisayara bugün yazılım da kurulamaz. Uygulama kataloğu modülü bunu çözer
  (bölüm 7.2).
- **Gruplar:** Bugün yalnızca laboratuvarlar var. İlk sürümde modül kapsamı için laboratuvarlar kullanılır.

## 3. Kavramlar

| Kavram | Anlamı |
| --- | --- |
| **Çekirdek** | Her kurulumda açık, kapatılamaz: cihazlar ve laboratuvarlar, kayıt ve cihaz kimliği, ajan güncelleme, denetim kaydı, kullanıcılar ve roller, bildirimler, sunucu sağlığı ve yedek. |
| **Modül** | Açılıp kapanabilen özellik. Bir kimliği, bağımlılıkları, ayarları, izinleri ve (varsa) ajan tarafı vardır. |
| **Etkinlik** | Modül bu kurumda ya da bu laboratuvarda kullanılıyor mu. Kapsam: kurum geneli → laboratuvar. En özel ayar kazanır. |
| **İzin** | Kim kullanabilir: rol + modül izni (ör. `vision.izle`, `vision.kontrol`). Yönetici rolleri kurum geneli; **öğretmen** rolü yalnızca atandığı laboratuvarlarda (bölüm 6.1). |
| **Hazır olma** | Modülün ihtiyaç duyduğu şey kurulu ve çalışıyor mu (dış servis erişilebilir mi, ayarları tamam mı). Hazır olmayan modül panelde "Kurulum gerekli" kartıyla görünür. |
| **Profil** | Kurulumda seçilen başlangıç ayarları ("Okul laboratuvarı", "Kurum"). Sonradan modül modül değiştirilebilir. |

## 4. Modül listesi (ilk sürüm)

| Modül | Kimlik | Bağımlılık | Ajan tarafı | Okul laboratuvarı | Kurum |
| --- | --- | --- | --- | --- | --- |
| Uzaktan ekran (Vision) | `vision` | – | yerel Vision yeteneği | Açık | Kapalı |
| Uzak komut (terminal) | `terminal` | – | yerel terminal yeteneği | Açık | Kapalı |
| Yazılım dağıtımı (paket zinciri, serbest komut) | `deploy` | `terminal` | `execute` | Açık | Kapalı |
| Uygulama kataloğu (yeni) | `catalog` | – | `install_package` | Açık | Açık |
| Zamanlanmış görevler | `schedules` | `terminal` ya da `catalog` | – | Açık | Açık |
| Windows Update yönetimi | `patches` | – | tarama ve kurulum | Açık | Açık |
| Yazılım envanteri | `software` | – | envanter raporu | Açık | Açık |
| Lisanslar | `licenses` | `software` | – | Kapalı | Açık |
| Yardım masası | `helpdesk` | – | tepside "Sorun bildir" | Kapalı | Açık |
| DNS politikası | `dns_policy` | – | tespit ve eşik | Açık | Kapalı |
| Karantina | `quarantine` | – | kilit ekranı, yalıtım | Açık | Açık |
| Wake-on-LAN | `wol` | – | eş cihazdan uyandırma | Açık | Açık |
| Raporlar | `reports` | – | – | Açık | Açık |
| Kurumsal giriş (OIDC/LDAP) (yeni) | `sso` | – | – | Kapalı | Açık |
| SIEM'e kayıt gönderme (yeni) | `siem` | – | – | Kapalı | Açık |

Profil sütunları yalnızca **varsayılandır**. Örneğin kurumda eğitim salonu laboratuvarında Vision açılabilir,
muhasebede kapalı kalır.

**Mevcut kurulumlar** yükseltmede davranış değiştirmez: bugün çalışan her modül açık başlar, profil "Özel" görünür.

## 5. Veri modeli (öneri)

```sql
-- Modül ayarı: kapsam 'org' (tek satır) ya da 'lab' (lab adı). En özel kapsam kazanır.
CREATE TABLE module_settings (
    module_id  TEXT NOT NULL,
    scope_type TEXT NOT NULL CHECK (scope_type IN ('org', 'lab')),
    scope_id   TEXT NOT NULL DEFAULT '',     -- org için '', lab için lab adı
    enabled    BOOLEAN,                      -- NULL: üst kapsamdan devral
    config     JSONB NOT NULL DEFAULT '{}',  -- modüle özel ayarlar (gizli değerler secretbox ile)
    updated_by TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (module_id, scope_type, scope_id)
);
-- Seçilen profil (bilgi amaçlı; ayarlar module_settings'tedir)
INSERT INTO global_settings (key, value) VALUES ('install_profile', 'custom');
```

Modüllerin kendisi (ad, açıklama, bağımlılıklar, izinler, ajan eylemleri, hazır olma denetimi) kodda bir kayıt
listesinde tanımlanır (`Backend/pops/modules.py`), veritabanında değil. Bu sayede modül eklemek bir migration değil,
kod değişikliğidir ve testle gelir.

## 6. Üç katmanda uygulama

1. **Sunucu (asıl karar):**
   - Her uç ve WebSocket mesajı bir modüle bağlanır: `Depends(require_module("vision"))`. Cihaza yönelik işlemde
     cihazın laboratuvarındaki etkin ayar kullanılır.
   - Kapalı modül: `409 {"module": "vision", "state": "disabled"}`. Hazır olmayan: `state: "not_ready"`.
   - Zamanlayıcı ve kuyruk, kapalı modülün işlerini başlatmaz. Kapanan modülün açık Vision oturumları kapatılır,
     bekleyen görevleri "Denied" olur.
2. **Panel:**
   - Menü ve sayfalar etkin modüllere göre çizilir (`GET /api/modules` her modülün durumunu ve kullanıcının iznini
     döner).
   - Kapalı modülün sayfası açılırsa "Bu modül kapalı" kartı gösterilir. Hazır değilse "Kurulum gerekli" kartı ve
     ayar bağlantısı çıkar.
   - Yeni sayfa **Sistem → Modüller**: kurum geneli tablo, laboratuvar bazında istisnalar, her değişiklik onaylı ve
     denetim kaydına yazılır.
3. **Ajan:**
   - Sunucu, ajan politikasıyla birlikte cihazın etkin modül listesini gönderir (mevcut `agent_policies` kanalı).
   - Ajan kapalı modülün eylemini reddeder. Yerel yetenek (terminal, Vision) her zaman son sözdür:
     **etkin = sunucuda açık VE bilgisayarda izinli**.
   - Ajan kapalı modülün yan işlerini de durdurur (ör. DNS izleme, tepsideki "Sorun bildir").

### 6.1 Öğretmen rolü ve öğretmen bilgisayarı (ilk sürümde)

- **Rol `teacher` (öğretmen):** bir ya da birkaç laboratuvara atanır (`user_labs`: kullanıcı ↔ laboratuvar).
  Panelde ve API'de yalnızca o laboratuvarların cihazlarını görür.
- **İzinleri:**
  - kendi laboratuvarının bilgisayarlarının durumu ve ekran önizlemeleri;
  - Vision ile **yalnızca izleme** (onay ya da duyuru kuralları aynen geçerli, uzaktan girdi yok);
  - kendi laboratuvarını uyandırma (Wake-on-LAN).
- **İzni olmayanlar:** uzak komut, dağıtım, karantina, ayarlar, kullanıcılar, diğer laboratuvarlar. Sunucuda
  varsayılan "yasak": yönetici gerektiren bütün uçlar öğretmeni zaten reddeder; öğretmen yalnızca açıkça izin
  verilen ve laboratuvar denetimi yapılan uçlara erişir. Her erişim kendi laboratuvarı için denetlenir (başka
  laboratuvarın cihaz kimliğini bilmek yetmez).
- **Öğretmen bilgisayarı (ana cihaz):** her laboratuvarın bir öğretmen bilgisayarı olabilir
  (`lab_settings.main_pc`).
  - Bugün bilgisayar adıyla tutuluyor; ad değişince bağ kopuyor. Donanım kimliğine (`HW-…`) geçirilir; migration
    eşleşen adları dönüştürür.
  - Laboratuvar planında "Öğretmen bilgisayarı" olarak işaretlenir ve öğretmenin izlediği bilgisayarlar listesine
    girmez.
  - İleride öğretmen ekranı bu bilgisayarın tepsisinden açılabilir (öğretmen modu kararıyla birlikte).
### 6.2 Zamanlı erişim: ders programına göre açılır, kapanır

Üniversite ve okullarda üç bilgi zaten vardır: kim olduğu (kurumun giriş sistemi), hangi derslikte yetkili olduğu ve
ne zaman dersi olduğu (ders programı). POps bunları birleştirir: öğretmenin erişimi **ders saatinde kendiliğinden
açılır, ders bitince kapanır**; yetki kalıcı değil, yalnızca gereken saatte vardır.

- **Erişim kaydı** (`lab_access_grants`): öğretmen × laboratuvar × zaman.
  - Zaman, haftalık tekrar eden bir aralık (gün, başlangıç, bitiş, dönem başı/sonu) ya da tek seferlik bir aralık
    olabilir.
  - Kaynak `manual` (BT ekledi), `import` (ders programından) ya da `sso` (giriş sisteminden gelen grup) olarak
    tutulur.
- **Tolerans:** kurum ayarı, ör. dersten 10 dk önce açılır, bittikten 15 dk sonra kapanır.
- **Ders programı içe aktarma:** önce CSV/Excel (ders, laboratuvar, gün, saat, öğretim elemanı kullanıcı adı ya da
  e-postası, dönem). Üniversite bilgi sistemleri (UBYS, OBS vb.) ve okullarda e-Okul ders programı bu biçime
  dışa aktarılabilir. Belirli bir sisteme doğrudan bağlayıcı, talep olursa sonra.
  - Her içe aktarma önce önizlenir: eşleşmeyen öğretim elemanları ve bilinmeyen laboratuvarlar listelenir, onayla
    uygulanır.
  - Yeni dönem içe aktarıldığında eski dönemin kayıtları kapanır.
- **Kimlik eşleme:** kurumsal girişten (bölüm 7.1) gelen kullanıcı adı ya da e-posta, ders programındaki öğretim
  elemanıyla eşleşir. Kurumsal giriş yoksa öğretmen POps'ta yerel kullanıcı olarak açılır, eşleme kullanıcı adıyla
  yapılır.
- **Uygulama:**
  - Öğretmenin "şu an yetkili olduğu laboratuvarlar" her istekte ve panel bağlantısının 10 saniyelik yeniden
    denetiminde hesaplanır.
  - Süre dolunca açık Vision izlemesi kapanır, laboratuvar listeden düşer; önizleme ve uyandırma reddedilir.
  - Açılma ve kapanmalar denetim kaydına yazılır ("Ali Hoca, Lab-3: erişim açıldı 09:50, kapandı 12:05").
- **BT yöneticileri** (admin, superadmin) saatten bağımsızdır.
- **Saat:** sunucunun saat dilimine göre (zamanlanmış görevlerle aynı).

- Öğretmen modülü bağımsız bir modül değildir; `vision` ve `wol` modüllerinin laboratuvardaki etkin ayarına uyar.
  Vision o laboratuvarda kapalıysa öğretmen de izleyemez.

Eski ajanlar modül listesini bilmez. Sunucu onlar için de 1. katmanda karar verdiği için güvenlik açığı oluşmaz;
yalnızca tepsideki öğeler ajan güncellenene kadar görünmeye devam eder.

## 7. Yeni modüller

### 7.1 Kurumsal giriş (`sso`): OIDC, sonra LDAP

- **OIDC** (Keycloak, Microsoft Entra, Google Workspace vb.): yetkilendirme kodu akışı + PKCE.
  - `/api/auth/oidc/start` IdP'ye yönlendirir.
  - `/api/auth/oidc/callback` kimlik jetonunu JWKS ile doğrular (PyJWT + cryptography, yeni bağımlılık yok) ve
    bugünkü `pops_jwt` çerezini verir.
- **Rol eşleme:** IdP grubu → POps rolü ve modül izinleri (ör. `pops-admins` → admin). Eşleşmeyen kullanıcı
  giremez; ilk girişte kullanıcı kaydı oluşur, sonraki girişlerde grubu yeniden okunur ve rol değişmişse oturum
  sürümü artar.
- **2FA:** OIDC kullanıcısının çok adımlı doğrulaması IdP'dedir. Yerel kullanıcılar TOTP'yi kullanmaya devam eder.
- **Acil durum hesabı:** En az bir yerel superadmin her zaman kalır (IdP çökerse panel kilitlenmesin).
- **LDAP/AD:** ikinci adım, `ldap3` (saf Python) ile; aynı rol eşleme.
- **Hazır olma:** IdP keşif adresi (`.well-known/openid-configuration`) erişilebilir ve ayarlar tamam.

### 7.2 Uygulama kataloğu (`catalog`)

- **Amaç:** Yönetici seçilmiş uygulamaları bir laba ya da cihazlara kurar. **Terminali kapalı bilgisayarda da
  çalışır**, çünkü ajan keyfi komut değil yalnızca katalogdaki bir paketi kurar.
- **Ajan eylemi** `install_package {package_id, version, source, sha256}`. Ajan komut satırını kendisi kurar;
  sunucudan komut metni gelmez.
- **İki kaynak:**
  1. **Kendi paketlerimiz:** sunucuya yüklenen MSI/EXE. İmzalı bağlantı ve SHA-256 denetimi zaten var. İnternetsiz
     okulda da çalışır.
  2. **winget:** paket kimliğiyle. Risk: winget'in SYSTEM hesabında çalıştırılması uğraştırıcıdır, önce küçük bir
     prototip gerekir. Olmazsa yalnızca birinci kaynakla başlarız.
- **Politika:** "Bu laboratuvarda şu uygulama şu sürümde bulunmalı". Zamanlayıcı yazılım envanteriyle
  karşılaştırır, eksik olanı kurar, raporlar.

### 7.3 SIEM'e kayıt gönderme (`siem`)

- **Ne gider:** Hash zincirli denetim kaydı (`device_audit_logs`, her satır `entry_hash` ile) ve güvenlik olayları
  (`agent_logs_v2`'de risk seviyesi orta ve üstü).
- **Nasıl:**
  - Syslog (RFC 5424, TCP+TLS ya da UDP) ya da HTTP JSON (Wazuh, Graylog, Elastic, Splunk HEC).
  - Gönderilen son satır bir imleçte tutulur; alıcı yoksa imleç ilerlemez, sonra kaldığı yerden devam eder (en az
    bir kez).
- **Ek fayda:** Denetim zincirinin özetleri sunucu dışına çıkar. Biri veritabanında zinciri baştan yazsa bile dış
  kopyayla karşılaştırılınca fark ortaya çıkar. Bu, açık kalan R-07'nin "dış sabitleme" kısmını karşılar.
- **Hazır olma:** Son gönderim başarılı ve imleç güncel (gecikme eşiği aşılırsa uyarı).

## 8. Profil seçimi

- **Yeni kurulum:** panelin ilk açılışında kısa bir sihirbaz ("Bu kurulum nerede kullanılacak? Okul laboratuvarı /
  Kurum / Özel"). Seçim yalnızca varsayılanları yazar; sonra her şey Sistem → Modüller'den değişir.
- **Profili sonradan değiştirmek:** elle yapılmış ayarlar korunur. Değişecek satırlar önce listelenir, onayla
  uygulanır.
- `install.sh` profil sormaz (sunucu kurulumu ile ürün kararı ayrı kalsın).

## 9. Uygulama sırası

| Faz | İçerik | Kim |
| --- | --- | --- |
| 0 | .NET 10 geçişi (devam ediyor) | LOCAL |
| 1a | Modül kaydı, `module_settings`, sunucu ve panel uygulaması, Sistem → Modüller, profiller, mevcut özelliklerin modüle bağlanması | Sunucu |
| 1b | Öğretmen rolü, zamanlı erişim kayıtları, ders programı CSV içe aktarma, öğretmen bilgisayarının donanım kimliğine geçmesi | Sunucu |
| 1b | Ajanın modül listesini alıp uygulaması (tepsi öğeleri, DNS izleme) | LOCAL |
| 2 | SIEM'e kayıt gönderme (yalnızca sunucu, en kısa) | Sunucu |
| 3 | OIDC girişi; ardından LDAP (ders programındaki öğretim elemanlarıyla otomatik eşleme) | Sunucu |
| 4 | Uygulama kataloğu: önce kendi paketlerimiz, sonra winget prototipi | Sunucu + LOCAL |

Her faz ayrı PR ve ayrı sürüm. Her biri testle ve belgeyle gelir.

## 10. Kararlar (2 Ekim 2026)

1. **Gruplar:** ilk sürümde modül kapsamı yalnızca laboratuvar. Departman gibi bağımsız gruplar gerekirse sonra.
2. **Öğretmen rolü ilk sürümde:** laboratuvarın öğretmen bilgisayarı (ana cihaz) kaydı düzeltilerek; bölüm 6.1.
3. **Mevcut kurulumlar:** yükseltmede bütün modüller açık başlar, davranış değişmez.
4. **Katalog:** önce yalnızca sunucuya yüklenen kendi paketlerimiz; winget prototipten sonra.
5. **Kapatılan modülün verisi** silinmez, yalnızca gizlenir; silme saklama süresi kurallarına kalır.
