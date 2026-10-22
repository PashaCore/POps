# Pilot okul kurulumu

Bu sayfa, POps'u bir okulda **tek bir laboratuvarda, 10 bilgisayarla** denemek için bir kontrol listesidir. Hazırlık
kurulum gününden önce yapılır; kurulum gününün kendisi yaklaşık bir saat sürecek biçimde planlanmıştır. Ardından
2–4 hafta boyunca neyin değiştiğini ölçersiniz.

> Süreler bir plandır, ölçüm değildir. İlk pilotunuzda gerçek süreleri not edip bu sayfayı güncellememize yardım
> ederseniz seviniriz. POps alfa aşamasındadır; pilotu öğrencilerin sınav ya da önemli bir ders için bilgisayarlara
> mutlaka ihtiyaç duyduğu bir haftaya denk getirmeyin.

## Roller

| Kim | Ne yapar |
| :--- | :--- |
| BT sorumlusu | Sunucuyu kurar, ajanları kurar, testleri yapar, ölçümleri tutar. |
| Okul yönetimi | Veri sorumlusu olarak KVKK aydınlatma metnini onaylar, pilota izin verir. |
| Bir öğretmen (isteğe bağlı) | Laboratuvarı kullanan biri olarak gözlemlerini ve geri bildirimini verir. |

## Kurulum gününden önce

Bunları kurulumdan bir iki hafta önce bitirin.

### Sunucu

- [ ] systemd'li bir Linux sanal makinesi hazırlayın. Denenmiş dağıtımlar: AlmaLinux/RHEL/Rocky ve Debian/Ubuntu.
      500 bilgisayara kadar önerilen boyut 2 vCPU, 4 GB bellek ve 40 GB SSD'dir
      ([kapasite raporu](../kapasite/README.md#5-donanım-önerisi)); pilot için fazlasıyla yeter.
- [ ] Sunucu, laboratuvar ağından 443 numaralı porttan erişilebilir olsun. Araya bir vekil sunucu (proxy) ya da
      güvenlik duvarı giriyorsa WebSocket yükseltmesine ve uzun süreli bağlantılara izin vermeli; TLS denetimi
      (TLS inspection) POps adresi için kapalı olmalı.
- [ ] Sunucuyu kurun (kurulum gününden önce yapılabilir):

  ```bash
  git clone https://github.com/PashaCore/POps.git
  cd POps
  sudo POPS_DOMAIN=pops.okul.local Installer/server/install.sh
  ```

  Betik sonunda panelin **admin şifresini** yazar; güvenli bir yere kaydedin. Klonlanan klasörü silmeyin, panel
  oradan sunulur. Ayrıntılar: [`docs/quick-start.md`](../quick-start.md) (İngilizce).
- [ ] Başka bir makineden denetleyin: `curl https://pops.okul.local/api/health` komutu `{"status":"ok",…}`
      döndürmeli (okulun kendi sertifika otoritesinde `--cacert pops-ca.pem` ekleyin).

### DNS

- [ ] Sunucu için bir ad seçin (örneğin `pops.okul.local`) ve okulun DNS sunucusunda sunucunun IP adresine
      yönlendirin. Laboratuvardaki bir bilgisayardan `nslookup pops.okul.local` ile denetleyin.
- [ ] **IP adresi değil, ad kullanın** ve sonradan değiştirmeyin: ad, her ajanın ayarına yazılır.

### Sertifika

İki yoldan birini seçin ([`docs/tls.md`](../tls.md), İngilizce):

- [ ] **Okulun kendi sertifika otoritesi** (`TLS_MODE=internal`, varsayılan; internet gerekmez). Kurulum bir
      sertifika otoritesi ve sunucu sertifikası oluşturur. Bu durumda:
  - `pops-ca.pem` dosyasını `https://pops.okul.local/pops-ca.pem` adresinden alın ve parmak izini sunucuda
    `sudo pops-tls show` çıktısıyla karşılaştırın;
  - dosyayı ajan kurulumunda kullanmak üzere bir USB belleğe ya da paylaşım klasörüne koyun (`SERVER_CA_CERT=`);
  - yöneticilerin tarayıcısında panelin uyarı vermemesi için aynı dosyayı yönetici bilgisayarlarına
    *Güvenilen Kök Sertifika Yetkilileri* olarak ekleyin.
- [ ] **Let's Encrypt** (`TLS_MODE=letsencrypt`): ad internetten bu sunucuya çözülmeli, 80 ve 443 numaralı portlar
      internetten erişilebilir olmalı. Bu durumda ajan kurulumunda `SERVER_CA_CERT` **vermeyin**.

### Hesaplar

- [ ] `https://pops.okul.local/` adresinden `admin` olarak girin. **Ayarlar → Kullanıcılar**'da kendi kullanıcınıza
      tıklayıp **Şifreyi sıfırla** ile şifreyi değiştirin, sonra **Ayarlar → Güvenlik**'te iki adımlı doğrulamayı
      kurun.
- [ ] Ortak hesap kullanmayın; her kişiye kendi hesabını açın (**Kullanıcı ekle**). Denetim kaydı kişiyi gösterir.
  - **Süper admin:** yalnızca BT sorumlusu.
  - **Yönetici:** günlük işleri yapacak BT personeli.
  - **İzleyici:** okul yönetimi ya da gözlemci öğretmen; yalnızca seçilen sayfalara bakar, komut gönderemez, ekran
    göremez.

### KVKK bildirimi

- [ ] [`kvkk-aydinlatma.md`](../kvkk-aydinlatma.md) dosyasındaki şablonu okulunuza uyarlayın; köşeli parantezli
      yerleri (kurum adı, saklama süresi, başvuru adresi) doldurun.
- [ ] Metni hukukçunuza ya da KVKK sorumlunuza kontrol ettirin. Reşit olmayan öğrenciler için veli bilgilendirmesini
      birlikte değerlendirin.
- [ ] Metni laboratuvara asın ve öğrencilere, öğretmenlere duyurun. Kurulumdan sonra **Politikalar** sayfasındaki
      **Aydınlatma metni** alanına da yazabilirsiniz; tepsi metni kullanıcı "Okudum" diyene kadar gösterir.
- [ ] Saklama sürelerine karar verin (varsayılan: olay kayıtları ve biten görevler 365 gün, okunmuş bildirimler
      90 gün; **Sistem → Bildirimler ve saklama**).

### Bilgisayarlar ve paket

- [ ] 10 bilgisayarın Windows 10 ya da 11, 64 bit olduğunu ve yerel yönetici hesabıyla girilebildiğini denetleyin.
- [ ] Dondurma yazılımı (Deep Freeze, Shadow Defender) varsa kurulum günü bilgisayarları **çözülmüş** hâlde
      başlatma planını yapın. Ajan dondurmadan önce kaydedilmelidir
      ([ayrıntı](../../Agent/README.md#machines-with-freeze-software), İngilizce).
- [ ] [GitHub sürümler sayfasından](https://github.com/PashaCore/POps/releases) `POps-Agent-<sürüm>-win-x64.msi`
      dosyasını indirin. İsterseniz imzasını `manifest.json` ve `manifest.json.sig` ile doğrulayın
      ([`keys/README.md`](../../keys/README.md#doğrulama-elle)).
- [ ] Hangi bilgisayarda uzaktan terminal ve ekran izleme açık olacak, karar verin. Laboratuvar bilgisayarlarında
      genellikle açık kalır; öğretmen ya da personel bilgisayarlarında `TERMINAL_ENABLED=0 VISION_ENABLED=0` ile
      kapatılabilir. Sunucu bu özellikleri kapatabilir ama geri açamaz.

## Kurulum günü (yaklaşık 1 saat)

| Süre | Adım | Tamam sayılması için |
| :--- | :--- | :--- |
| 0:00–0:05 | Sunucu ve panel kontrolü: `curl https://pops.okul.local/api/health`, panele giriş. | Sağlık yanıtı `ok`; panel açılıyor. |
| 0:05–0:10 | Laboratuvarı ve jetonu oluşturun: **Sınıflar → Sınıf işlemleri → Yeni sınıf…** (örneğin `Lab-1`); **Sistem → Güvenlik → Ajan kaydı ve kimlik → Jeton üret** (Sınıf: `Lab-1`, Kullanım sayısı: 10, Geçerlilik: 72 saat). `ENROLL_TOKEN=…` değerini kopyalayın. | Jeton listede görünüyor. |
| 0:10–0:35 | Ajanı 10 bilgisayara kurun (aşağıdaki komut, her bilgisayarda yönetici komut isteminde). | Her kurulum hatasız bitiyor. |
| 0:35–0:40 | Kaydı denetleyin. | **Cihazlar**'da 10 bilgisayar çevrimiçi ve `Lab-1`'de; her bilgisayarın tepsisinde POps kalkan simgesi; **Kimlik zorlaması** kartı 10 kayıtlı ajan gösteriyor. |
| 0:40–0:45 | Bir komut deneyin: **Uzak komut**'ta `Lab-1`'i seçin, `hostname` yazıp Enter'a basın. | 10 bilgisayardan da çıktı geliyor; **İşlemler**'de 10 başarılı görev. |
| 0:45–0:55 | Vision'ı onayla deneyin (aşağıda). | Kabul edilen oturumda ekran görünüyor; reddedilen oturumda hiçbir şey görünmüyor; ikisi de kayıtlarda. |
| 0:55–1:00 | **Kimlik zorlaması**'nı açın (**Sistem → Güvenlik**), **Zorlamayı aç** ile onaylayın. Dondurma yazılımı varsa bilgisayarları şimdi dondurun. | Sunucu artık kayıtsız ajanları reddediyor. |

Ajan kurulum komutu (okulun kendi sertifika otoritesini kullanıyorsanız `SERVER_CA_CERT` ile). Kurulum günlüğü her
Windows'ta bulunan `C:\Windows\Temp` klasörüne yazılır:

```
msiexec /i POps-Agent-<sürüm>-win-x64.msi /qn /l*v C:\Windows\Temp\pops-msi-install.log SERVER_URL=https://pops.okul.local ENROLL_TOKEN=<jeton> SERVER_CA_CERT=D:\pops-ca.pem
```

Let's Encrypt kullanıyorsanız `SERVER_CA_CERT=…` kısmını çıkarın. Bilgisayar görünmezse:
[sorun giderme](../troubleshooting.md#a-pc-does-not-appear-or-shows-offline) (İngilizce).

### Vision testi (onaylı)

1. Bir bilgisayarın başına bir gönüllü oturur; ne yapılacağını önceden ona söyleyin.
2. **Uzak ekran**'da `Lab-1`'i seçin, bilgisayara tıklayın, **Canlı izle**'yi açın, **Kullanıcıya sor**'u seçin,
   bir gerekçe yazın ("pilot testi") ve **Oturumu başlat**'a basın.
3. Bilgisayarda sizin adınızı ve gerekçeyi gösteren bir soru çıkar. Gönüllü **kabul eder**; ekran panelde görünür.
   Oturumu kapatın.
4. Aynısını tekrarlayın; bu kez gönüllü **reddeder**. Panel reddi gösterir, ekran görünmez.
5. Gönüllü tepsi simgesinden son 30 günün işlemlerine baksın: iki oturum da listede olmalı.
6. Panelde **Kayıtlar**'da iki oturumun kaydını bulun.

### İsteğe bağlı testler

- **Karantina:** tek bir bilgisayarı karantinaya alın (**Cihazlar → Diğer → Karantinaya al**). Kilit ekranı açılır,
  bilgisayar yalnızca POps sunucusuna erişebilir. Panelden **Karantinayı Kaldır** ile geri alın; ağın geri geldiğini
  denetleyin.
- **Yazılım dağıtımı:** küçük bir MSI'ı (örneğin 7-Zip) **Dağıtım** ile önce bir, sonra 10 bilgisayara gönderin.
- **Sınav modu:** **Sınıflar**'da laboratuvarı seçin, **Sınıf işlemleri → Sınav modu…** ile 40 dakikalık bir sınav
  başlatın; izin listesine yalnızca sınav sitesini (örneğin `sinav.meb.gov.tr`) yazın. Bilgisayarda tepside
  mesajınız görünmeli; sınav sitesi açılmalı, başka siteler açılmamalı. Kutucuklarda bilgisayarların **Sınavda**
  göründüğünü denetleyin, sonra **Sınavı bitir** ile kapatın ve ağın geri geldiğine bakın. Sınav modu bir ağ
  kısıtlamasıdır, gözetim değildir: bilgisayarda yerel yönetici olan biri onu kapatabilir. Öğrenci hesaplarının
  standart kullanıcı olduğundan emin olun. Ayrıntılar: [`security.md`](../security.md#exam-mode) (İngilizce).
- **Geri alma tatbikatı:** güncelleme başarısız olursa önceki sürümün kendiliğinden geri geldiğini görmek içindir.
  Kurulu sürümden daha yeni imzalı bir sürüm gerektirir: bir bilgisayara önce bir önceki sürümü kurun, yönetici
  komut isteminde `type nul > C:\POpsData\secure\rollback-drill` ile işareti bırakın, sonra **Sistem →
  Güncellemeler**'den yeni sürümü yalnızca o bilgisayara gönderin. Sonuç `rolled_back` olmalı ve bilgisayar önceki
  sürümle çalışmaya devam etmeli. Adımlar ve ayrıntılar:
  [`Agent/README.md`](../../Agent/README.md#rollback-drill) (İngilizce). Başında, bilgisayarı gerekirse elle
  düzeltebilecek biri bulunsun.

## Pilottan sonra: 2–4 hafta ölçüm

Amaç, "işimize yaradı mı?" sorusuna sayılarla cevap vermek. Pilot başlamadan önce **bir hafta boyunca** aynı işleri
eski yöntemle yapıp sürelerini not edin; bu sizin "önce" sayılarınızdır.

### Ne ölçülür

| Ölçü | Nasıl | Nereden |
| :--- | :--- | :--- |
| Bir programı bütün laba kurma süresi | Başlangıçtan son bilgisayarın bitmesine kadar dakika | Kendi notunuz; POps'ta **İşlemler** |
| Envanter çıkarma süresi | Bütün labın donanım ve yazılım listesini hazırlamak | Kendi notunuz; POps'ta **Raporlar → CSV** |
| Uzaktan yardım isteği başına süre | Talebin gelişinden çözülmesine kadar | **Destek talepleri** |
| Laboratuvara gitmek zorunda kalınan iş sayısı | Haftalık sayı | Kendi notunuz |
| Arıza ve olay sayısı | Haftalık; türüne göre (donanım, yazılım, ağ, yazıcı) | **Destek talepleri**, **Kayıtlar** |
| Başarısız ya da yarım kalan işler | Haftalık | **İşlemler**, **Bildirimler** |
| Güncellemesi eksik bilgisayar sayısı | Haftanın son günü | **Raporlar** (Windows Update) |
| Çevrimiçi oranı | Ders saatlerinde açık olması gereken bilgisayarların yüzde kaçı panelde görünüyor | **Cihazlar**, **Sistem → Genel bakış** |
| KVKK soruları ve itirazlar | Gelen soru, itiraz sayısı ve konusu | Kendi notunuz |

Basit bir kayıt tablosu yeterli:

| Tarih | İş | Yöntem (eski / POps) | Süre (dk) | Not |
| :--- | :--- | :--- | ---: | :--- |
| | | | | |

### Sonuç toplantısı

Pilotun sonunda BT, okul yönetimi ve gözlemci öğretmenle 30 dakikalık bir toplantı yapın: sayılara bakın, geri
bildirim formlarını okuyun, devam edip etmemeye ve kaç laboratuvara genişleteceğinize karar verin. Sonuçları
paylaşmak isterseniz [vaka çalışması şablonunu](vaka-calismasi-sablonu.md) kullanın.

## Geri bildirim formu taslağı

Kâğıtta ya da okulun kullandığı bir form aracında hazırlayabilirsiniz. Kişisel veri toplamayın; yalnızca rol sorun.

**Rol:** BT sorumlusu / Okul yönetimi / Öğretmen / Öğrenci

**1'den 5'e puanlayın** (1: hiç katılmıyorum, 5: tamamen katılıyorum):

1. Laboratuvardaki işleri POps ile daha kısa sürede yaptım. *(BT)*
2. Hangi bilgisayarda ne olduğunu artık daha kolay görüyorum. *(BT, yönetim)*
3. Ekranıma bakılmadan önce bana soruldu ya da haber verildi. *(öğrenci, öğretmen)*
4. Tepsideki "son 30 gün" listesi neler yapıldığını anlamamı sağladı. *(öğrenci, öğretmen)*
5. POps, derslerde kullandığım araçlarla (örneğin Veyon) çakışmadı. *(öğretmen)*
6. Aydınlatma metni anlaşılırdı. *(herkes)*
7. Pilotu başka laboratuvarlara genişletmeyi öneririm. *(BT, yönetim)*

**Açık uçlu sorular:**

- En çok işe yarayan özellik hangisiydi?
- Sizi en çok ne zorladı ya da rahatsız etti?
- Eksik bulduğunuz bir şey var mı?
- Bir arıza ya da beklenmedik bir durum yaşadınız mı? Ne oldu?

Bulduğunuz hataları [GitHub'da](https://github.com/PashaCore/POps/issues/new/choose) bildirebilir, sorularınızı
[GitHub Discussions](https://github.com/PashaCore/POps/discussions)'ta sorabilirsiniz. Güvenlik açıklarını herkese
açık yazmayın: [`SECURITY.md`](../../SECURITY.md).

## Pilot biterse

POps'u kaldırmak isterseniz ajanı Windows'un **Uygulamalar** ekranından ya da `msiexec /x` ile kaldırın. Servis ve
program dosyaları silinir; `C:\POpsData` (cihaz kimliği) ve `C:\POpsLogs` bilinçli olarak kalır, böylece yeniden
kurulan bilgisayar aynı kimlikle döner. Bunları da silmek isterseniz elle silin.
