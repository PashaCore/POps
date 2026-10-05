# Vaka çalışması şablonu

Bu şablon, bir [pilot kurulumdan](pilot-okul.md) sonra sonuçları başka okullarla paylaşmak içindir. Köşeli
parantezli her yeri (`[...]`) kendi bilgilerinizle değiştirin. Bilmediğiniz ya da ölçmediğiniz bir sayıyı tahminle
doldurmayın; "ölçülmedi" yazın. Ölçülmüş küçük bir sayı, tahmin edilmiş büyük bir sayıdan daha inandırıcıdır.

Yayımlamadan önce sondaki [yayın izni](#yayın-izni-ve-kişisel-veri) bölümünü tamamlayın.

---

## [OKUL ADI]: POps ile [LABORATUVAR SAYISI] laboratuvar

**Özet** (en fazla üç cümle): [Okul türü] olan [okul adı], [tarih aralığı] arasında [bilgisayar sayısı] bilgisayarlık
[laboratuvar sayısı] laboratuvarda POps'u denedi. [En önemli sonuç, bir sayıyla.] [Okulun devam kararı.]

### Okul ve ortam

| | |
| :--- | :--- |
| Okul | [okul adı ya da "bir Anadolu lisesi" gibi anonim tanım] |
| İl / ilçe | [il / ilçe] |
| Okul türü | [ilkokul / ortaokul / lise / meslek lisesi / üniversite / diğer] |
| Öğrenci sayısı (yaklaşık) | [sayı] |
| Laboratuvar sayısı | [sayı] |
| Yönetilen bilgisayar sayısı | [sayı] |
| Windows sürümleri | [Windows 10 / 11, sayılarıyla] |
| Dondurma yazılımı | [yok / Deep Freeze / Shadow Defender / diğer] |
| BT ekibi | [kişi sayısı; tam zamanlı mı, öğretmen mi] |
| Pilot süresi | [başlangıç tarihi] – [bitiş tarihi] ([hafta sayısı] hafta) |
| POps sürümü | [ör. 0.1.22-alpha] |
| Sunucu | [sanal makine / fiziksel; vCPU, bellek, disk; işletim sistemi] |
| Sertifika | [okulun kendi sertifika otoritesi / Let's Encrypt / kurumun sertifikası] |
| Birlikte kullanılan araçlar | [ör. Veyon, okul ağ filtresi] |

### Neden POps'u denedik?

[Pilottan önceki durum: hangi araçlar kullanılıyordu, en çok hangi iş zaman alıyordu, hangi sorun tekrar ediyordu.
2–4 cümle.]

### Kurulum

- Hazırlık süresi: [saat]
- Kurulum günü süresi: [dakika]
- Karşılaşılan sorunlar ve çözümleri: [madde madde]
- Kullanılan özellikler: [envanter / yazılım dağıtımı / uzak komut / Vision / karantina / yardım masası / Wake-on-LAN /
  zamanlanmış görevler / raporlar / lisanslar]
- Uzaktan terminal ve ekran izlemenin kapalı olduğu bilgisayarlar: [sayı ve neden]

### Sonuçlar

"Önce" sayıları pilottan önceki [süre] boyunca, "sonra" sayıları pilotun son [süre] boyunca ölçüldü.

| Ölçü | Önce | Sonra | Nasıl ölçüldü |
| :--- | ---: | ---: | :--- |
| Bir programı bütün laba kurma süresi (dk) | [önce] | [sonra] | [ör. iki programın ortalaması, kronometreyle] |
| Envanter çıkarma süresi (saat) | [önce] | [sonra] | [yöntem] |
| Uzaktan yardım isteği başına süre (dk) | [önce] | [sonra] | [yöntem] |
| Laboratuvara gitmek zorunda kalınan iş sayısı (haftalık) | [önce] | [sonra] | [yöntem] |
| Arıza ve olay sayısı (haftalık) | [önce] | [sonra] | [yöntem] |
| Güncellemesi eksik bilgisayar sayısı | [önce] | [sonra] | [yöntem] |
| Ders saatlerinde çevrimiçi bilgisayar oranı (%) | [önce] | [sonra] | [yöntem] |
| BT'nin haftalık laboratuvar işi (saat) | [önce] | [sonra] | [yöntem] |

**Yorum:** [Sayılar ne anlatıyor? Neyin değişmediğini de yazın.]

### Geri bildirim

[Geri bildirim formundaki puanların ortalaması ve kaç kişinin katıldığı, rol rol. Açık uçlu cevaplardan öne
çıkanlar.]

| Soru | BT | Yönetim | Öğretmen | Öğrenci |
| :--- | ---: | ---: | ---: | ---: |
| [soru] | [ortalama / kişi sayısı] | [...] | [...] | [...] |

### KVKK ve şeffaflık

- Aydınlatma metni nasıl duyuruldu: [laboratuvara asıldı / velilere gönderildi / tepside gösterildi]
- Gelen soru ve itirazlar: [sayı ve konu]
- Onaylı ekran oturumu sayısı ve reddedilenler: [sayı / sayı]
- Öğrenci ve öğretmenlerin şeffaflık özelliklerine tepkisi: [kısa not]

### Ne iyi gitti, ne zorladı?

**İyi giden:**

- [madde]

**Zorlayan:**

- [madde]

**POps ekibine önerilerimiz:**

- [madde; varsa GitHub issue bağlantısıyla]

### Sonraki adımlar

[Devam kararı, genişletme planı, ne zaman yeniden ölçüleceği.]

### Alıntı

> "[Alıntı metni]"
>
> — [Ad Soyad ya da yalnızca rol], [görev], [okul] *(yalnızca kişinin yazılı izniyle)*

---

## Yayın izni ve kişisel veri

Yayımlamadan önce hepsini işaretleyin:

- [ ] Okul yönetimi bu metnin yayımlanmasına yazılı olarak izin verdi.
- [ ] Okulun adı yalnızca izin varsa yazıldı; yoksa anonim bir tanım kullanıldı.
- [ ] Metinde öğrenci adı, kullanıcı adı, bilgisayar adı, IP adresi ya da sunucu adresi yok.
- [ ] Ekran görüntülerinde öğrenci adı, yüzü, kullanıcı adı ya da okulun iç adresleri görünmüyor.
- [ ] Alıntı yapılan herkes yazılı izin verdi.
- [ ] Bütün sayıların "Nasıl ölçüldü" sütunu dolu; ölçülmeyenler "ölçülmedi" diye yazıldı.

Doldurulmuş vaka çalışmasını paylaşmak isterseniz
[GitHub Discussions](https://github.com/PashaCore/POps/discussions/categories/show-and-tell)'ta **Show and tell**
kategorisinde yayımlayabilir ya da proje ekibine e-postayla gönderebilirsiniz: [E-POSTA ADRESİ, proje sahibi
dolduracak].
