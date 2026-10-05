# Neden POps?

Bu sayfa okulların BT sorumluları ve yöneticileri içindir: POps hangi işi görür, hangisini görmez, kişisel veriye
nasıl yaklaşır, neye mal olur. Teknik ayrıntılar İngilizce belgelerdedir; bağlantılar her bölümün sonunda.

> **Önce bilinmesi gereken:** POps **alfa** aşamasında bir yazılımdır. Kayıttan imzalı güncellemeye ve geri almaya
> kadar bütün yol gerçek Windows bilgisayarlarda denendi, ama büyük ölçekli ya da kurumsal üretim ortamı için henüz
> sağlamlaştırılmış değildir. Kalan riskler [`SECURITY.md`](../../SECURITY.md) dosyasında açıkça yazılıdır. İlk adım
> olarak tek bir laboratuvarda pilot kurulum öneriyoruz: [Pilot okul kurulumu](pilot-okul.md).

## Kısaca

POps, okul laboratuvarlarındaki Windows bilgisayarları tek bir web panelinden yönetmenizi sağlayan açık kaynak bir
işletim platformudur. Envanter, uzaktan destek, yazılım dağıtımı, imzalı güncelleme, karantina ve "kim ne yaptı"
kaydı aynı yerdedir. Sunucu okulun kendi makinesinde çalışır; veriler okulda kalır.

POps'un temel ilkesi şudur:

> **Yöneticilerin güçlü araçları olmalı. Kullanıcılar da bu araçlar kullanıldığında her zaman bilmeli.**

## Hangi sorunu çözer?

Bir okulun BT ekibi laboratuvarı çoğu zaman birbirinden kopuk araçlarla ayakta tutar: envanter için bir tablo,
uzaktan yardım için bir program, kurulum dosyaları için bir paylaşım klasörü, lisanslar için başka bir tablo.
"Hangi bilgisayarda kim, ne zaman, ne yaptı?" sorusunun ise çoğu zaman cevabı yoktur. POps bunları bir araya getirir:

| İhtiyaç | POps'ta karşılığı |
| :--- | :--- |
| Hangi bilgisayar nerede, içinde ne var? | Donanım envanteri (işlemci, RAM, anakart, ekran kartı, diskler, Windows sürümü, IP, MAC), kurulu programlar ve Windows Update durumu. |
| Bir programı bütün laba kurmak | **Dağıtım** sayfası: MSI, ZIP ya da betik adımlarından bir zincir kurup bir laba gönderirsiniz. Dosyanın SHA-256 özeti çalıştırılmadan önce bilgisayarda denetlenir. |
| Bir komutu 30 bilgisayarda çalıştırmak | Görev kuyruğu: bir bilgisayara, bir laba ya da hepsine; duraklatma, iptal, yeniden deneme ve her bilgisayar için çıkış kodu. İstenirse her gün ya da seçili günlerde. |
| Uzaktan yardım | **Vision**: kullanıcının onayıyla canlı ekran ve uzaktan fare-klavye. |
| Lisans sayısını tutturmak | Kurulumlar satın alınan koltuk sayısıyla karşılaştırılır; süre bitmeden ve aşımda uyarı. |
| Öğrencinin sorunu bildirmesi | Tepsi simgesindeki **Sorun bildir** ile talep açılır, BT panelden yanıtlar. |
| Dondurma yazılımlı (Deep Freeze vb.) makineler | Ajan dondurmadan önce kaydedilirse her yeniden başlatmadan sonra kimliğini korur. |
| Kapalı bilgisayarları açmak | Wake-on-LAN ile bir bilgisayar, bir lab ya da hepsi. |
| Sorunlu bir bilgisayarı ağdan ayırmak | Karantina: kilit ekranı ve ağ yalıtımı; yalnızca POps sunucusu erişilebilir kalır. |
| Güncellemeyi güvenle yapmak | Ajan güncellemeleri imzalıdır; yeni sürüm çalışmazsa önceki sürüm kendiliğinden geri gelir. |

Ayrıntılı özellik listesi: [README](../../README.tr.md#-neler-yapabilirsiniz).

## Neyi çözmez?

Beklentiyi baştan doğru kurmak için POps'un **ne olmadığını** da yazıyoruz:

- **Ders yönetim aracı değildir.** Öğretmenin ders sırasında ekranları izlemesi, kendi ekranını sınıfa yansıtması
  ya da bilgisayarları kilitlemesi için Veyon gibi araçlar vardır. POps onların yerini almaz, altında çalışır:
  [POps ve Veyon birlikte](veyon-ile-birlikte.md).
- **Bugün yalnızca Windows 10 ve 11 (64 bit) yönetir.** Linux ve Pardus ajanı planlanıyor ama henüz yok; macOS,
  telefon ve tablet yönetimi de yok.
- **İçerik filtresi değildir.** DNS politikası, listelenen alan adlarına girişi **tespit eder** ve isterseniz eşiği
  aşan bilgisayarı karantinaya alır; siteleri engellemez. Engelleme için okulun ağ filtresi gerekir.
- **Henüz denenmemiş durumlar var:** Uzak Masaüstü ve çok kullanıcılı oturumlar, uzaktan kontrol sırasında UAC onay
  ekranı, birden fazla monitör ve %100 dışındaki ekran ölçekleme.
- **Active Directory / LDAP ile giriş yok.** Panel hesapları POps'un kendi kullanıcılarıdır.
- **Panel Türkçedir.** İngilizce arayüz hazırlanıyor; bazı bölümler çevrildi, diğerleri sırayla geliyor. Teknik
  belgelerin çoğu İngilizcedir.
- **Destek modeli henüz belirlenmedi.** Bugün destek GitHub üzerinden, gönüllülük esasıyladır; yanıt süresi sözü
  verilmez. Ücretli destek ve hizmet seviyesi seçenekleri [`ROADMAP.md`](../../ROADMAP.md#support-model) dosyasında
  karar bekliyor.

## KVKK ve şeffaflık

Okulda klavyenin başındaki kişi çoğu zaman reşit olmayan bir öğrencidir. KVKK, ekranına bakıldığında bunu bilmesini
bekler. POps şeffaflığı sonradan eklenen bir ayar olarak değil, tasarımın parçası olarak ele alır:

- **Önce onay.** Rutin bir canlı ekran oturumu, kullanıcı yöneticinin adını ve gerekçeyi gösteren soruyu kabul
  edince başlar. Zorunlu oturumda (örneğin sınav sırasında) tam ekran bir geri sayım gösterilir ve gerekçe kaydedilir.
- **Önizlemeler duyurulur.** Ekran önizlemeleri onay istemez, ama tepsi simgesinin ipucu son önizlemenin saatini
  gösterir ve en fazla beş dakikada bir bildirim çıkar.
- **Gizli hiçbir şey yok.** Tepsi simgesi her zaman görünür; gizli mod ve tuş kaydı yoktur. Canlı ekran kareleri
  saklanmaz, yalnızca oturumu açan yöneticiye gider.
- **"BT bu bilgisayarda ne yaptı?"** Tepsi, son 30 günün uzaktan oturumlarını, komutlarını ve karantinalarını
  listeler. Aynı işlemler sunucudan bağımsız olarak bilgisayarın Windows olay günlüğüne de yazılır.
- **Kim ne yaptı, kanıtıyla.** Güvenlikle ilgili her işlem (oturum başlatma, karantina, komut, güncelleme) yapan
  kişiyle birlikte, değiştirildiğinde fark edilen bir hash zincirine yazılır.
- **Kapalı demek kapalı.** Personel bilgisayarları gibi uzaktan terminale ya da ekran izlemeye ihtiyaç olmayan
  bilgisayarlarda bu özellikler kurulumda kapatılabilir (`TERMINAL_ENABLED=0`, `VISION_ENABLED=0`). Sunucu bunları
  kapatabilir ama asla geri açamaz; sunucu ele geçirilse bile o bilgisayarda komut çalıştırılamaz.
- **Saklama süreleri ayarlanabilir.** Ajan olay kayıtları ve biten görevler varsayılan olarak 365 gün, okunmuş
  bildirimler 90 gün sonra silinir. Güvenlik denetim zinciri silinmez.
- **Aydınlatma metni.** Veri envanteri ve uyarlanacak bir metin şablonu hazırdır:
  [`kvkk-aydinlatma.md`](../kvkk-aydinlatma.md). **Politikalar** sayfasındaki aydınlatma metni, tepside kullanıcı
  "Okudum" diyene kadar gösterilir. Metni yayımlamadan önce kurumunuzun hukukçusuna kontrol ettirin.

Veriler okulun kendi sunucusundaki PostgreSQL veritabanında tutulur. Sunucunun dışarıya açtığı bağlantılar
şunlardır: GitHub'dan yeni sürüm denetimi ve sürüm notları (internet yoksa atlanır) ve yalnızca siz ayarlarsanız
bildirim e-postası ya da webhook. Bilgisayarlardaki ajanlar yalnızca kurulumda verilen POps sunucusuna bağlanır.

Ayrıntılar (İngilizce): [`docs/security.md`](../security.md), [`docs/vision.md`](../vision.md).

## Maliyet

- **Lisans ücreti yok.** POps [Apache 2.0](../../LICENSE) lisanslıdır; kaynak kodu açıktır, kopyalanabilir ve
  değiştirilebilir.
- **Sunucu:** systemd'li bir Linux sanal makinesi yeter. Ölçümlere göre 500 bilgisayara kadar 2 vCPU, 4 GB bellek
  ve 40 GB SSD önerilir ([kapasite raporu](../kapasite/README.md)). Var olan bir sanallaştırma altyapısında ek
  donanım gerekmeyebilir.
- **Bilgisayarlar:** ek bir şey gerekmez; ajan kendi .NET çalışma zamanını getirir.
- **Sertifika:** okulun kendi sertifika otoritesi kurulumla birlikte oluşturulur; ücretsizdir ve internet gerektirmez.
  Let's Encrypt de ücretsizdir.
- **Asıl maliyet emektir:** sunucuyu kurmak, yedekleri izlemek ve güncellemeleri uygulamak okulun BT ekibinin
  işidir. Kurulum tek bir betikle yapılır; sunucu ve ajanlar panelden güncellenir.

## İnternet olmadan

POps okul ağının içinde, internet olmadan çalışacak biçimde tasarlandı:

- Varsayılan kurulum, sertifikayı sunucuda oluşturulan okul sertifika otoritesiyle (internal CA) kurar.
- Ajan güncellemeleri GitHub'dan indirilemezse panelden elle yüklenebilir; imza yine denetlenir.
- Sunucu GitHub'a ulaşamıyorsa sürüm denetimi atlanır, gerisi çalışmaya devam eder.
- Panel, 0.1.22'den itibaren yazı tiplerini ve simgeleri kendi sunucusundan yükler; yöneticinin tarayıcısı internete
  çıkmaz. 0.1.21 ve öncesinde yazı tipleri ve simgeler CDN'den gelir; internet yoksa panel çalışır ama bazı
  simgeler görünmez.

## Bir ders yönetim aracından farkı

| | Ders yönetim aracı (ör. Veyon) | POps |
| :--- | :--- | :--- |
| Kim kullanır? | Öğretmen | BT sorumlusu, laboratuvar yöneticisi |
| Ne zaman? | Ders sırasında açılır, ders bitince kapanır | Sürekli; bilgisayarlar açık olduğu sürece |
| Ne için? | Dersi yönetmek: ekranları izlemek, ekranını yansıtmak, kilitlemek | Laboratuvarı işletmek: envanter, dağıtım, güncelleme, denetim, uzaktan destek |
| Ekran görme | Dersin parçası | Destek aracı; onay ya da duyuru, gerekçe ve denetim kaydıyla |

İkisi aynı bilgisayarda yan yana çalışabilir. Nasıl yapılacağı: [POps ve Veyon birlikte](veyon-ile-birlikte.md).
Karşılaştırmanın İngilizce aslı: [`docs/positioning.md`](../positioning.md).

## Sonraki adım

1. [Pilot okul kurulumu](pilot-okul.md): 10 bilgisayarlık bir lab için yaklaşık bir saatlik kontrol listesi.
2. Pilottan sonra sonuçları paylaşmak isterseniz: [vaka çalışması şablonu](vaka-calismasi-sablonu.md).
3. Sorular için: [GitHub Discussions](https://github.com/PashaCore/POps/discussions). Güvenlik açıklarını herkese
   açık yazmayın; [`SECURITY.md`](../../SECURITY.md) dosyasındaki adrese bildirin.
