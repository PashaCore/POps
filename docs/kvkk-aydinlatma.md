# KVKK — Aydınlatma Metni Şablonu ve Veri Envanteri

> **Bu bir şablondur, hukuki tavsiye değildir.** POps ekran görüntüsü ve cihaz
> kullanım verisi işleyebildiği ve okul ortamında **veri sahipleri çoğunlukla reşit
> olmayan öğrenciler** olduğu için, bu metni kurumunuza uyarlayın ve yayımlamadan önce
> **bir hukukçuya / KVKK uyum sorumlusuna kontrol ettirin.** POps'un "Transparent by
> Design" ilkesi burada bir slogan değil, bir uyum aracıdır.

---

## 1. Veri envanteri — POps hangi veriyi işler?

| Veri | Nerede | Neden | Saklama | Kim görür |
|------|--------|-------|---------|-----------|
| Cihaz envanteri (CPU, RAM, disk, OS, MAC, IP) | Sunucu DB | Varlık yönetimi | Kurum belirler | BT yöneticisi (panel) |
| Cihaz kimliği (HWID) + hostname | Sunucu DB | Cihazı tanımlama | Kurum belirler | BT yöneticisi |
| **Oturum açan kullanıcı adı** (`logged_user`) | Sunucu DB | Hangi makinede kim var | Kurum belirler | BT yöneticisi |
| **Ekran görüntüsü / canlı ekran (POpsVision)** | Yalnızca izleme anında akar; **kalıcı kaydedilmez** (varsayılan) | Uzaktan destek / denetim | Akış anlıktır | Oturumu başlatan yönetici |
| Ön plandaki programın adı (ör. `chrome`; **pencere başlığı alınmaz**) | Sunucu DB (`active_window`, yalnızca son değer) | Anlık durum | Bir sonraki bildirimle değişir | BT yöneticisi |
| Kurulu yazılımlar (ad, sürüm, yayıncı, kurulum tarihi) ve Windows Update durumu | Sunucu DB | Varlık ve yama yönetimi | Kurum belirler | BT yöneticisi |
| Denetim kayıtları (kim, ne zaman, hangi işlem) | Sunucu DB (`device_audit_logs` / `agent_logs_v2`) | Hesap verebilirlik | Kurum belirler | BT yöneticisi |
| Politika ihlali uyarıları (DNS/kategori) | Sunucu DB | İçerik politikası | Kurum belirler | BT yöneticisi |

**POps'un TOPLAMADIĞI:** tuş kaydı (keylogger yoktur), dosya içerikleri, kişisel
dosya/tarayıcı geçmişi. Ekran görüntüsü **gizlice** alınmaz — her önizleme tepsi
ipucunu günceller ve en fazla 5 dakikada bir bildirim gösterir; canlı oturum onay
ister ya da zorunlu oturumda geri sayım + gerekçe gösterir; tepsi simgesi her zaman
görünür, gizli mod yoktur.

---

## 2. Aydınlatma metni — doldurulacak şablon

> **[KURUM ADI] Bilişim Sistemleri Kullanımı Aydınlatma Metni**
>
> 6698 sayılı Kişisel Verilerin Korunması Kanunu ("KVKK") kapsamında, veri sorumlusu
> sıfatıyla **[KURUM ADI]** olarak, laboratuvar/sınıf bilgisayarlarında kullanılan
> **POps** bilişim yönetim platformu aracılığıyla işlenen kişisel verilerinize ilişkin
> sizi bilgilendiririz.
>
> **İşlenen veriler:** oturum açan kullanıcı adı, cihaz ve kullanım bilgileri, denetim
> kayıtları ve — yalnızca bir yönetici tarafından başlatıldığında ve size bildirim
> gösterilerek — ekranınızın anlık görüntüsü. Tuş kaydı yapılmaz.
>
> **İşleme amaçları:** kurum bilişim altyapısının güvenli ve düzenli işletilmesi,
> yazılım dağıtımı ve güncelleme, arıza/uzaktan destek, güvenlik ve içerik politikası
> ile denetim yükümlülükleri.
>
> **Hukuki sebep:** KVKK m.5/2 (veri sorumlusunun meşru menfaati ve/veya hukuki
> yükümlülüğü). **Reşit olmayan** öğrenciler için işleme, veli/vasi bilgilendirmesi ve
> gereken hallerde açık rıza ile birlikte değerlendirilmelidir — **[hukukçu onayı]**.
>
> **Saklama süresi:** [X gün/ay]. Süre sonunda veriler silinir/anonimleştirilir.
>
> **Aktarım:** veriler kurum içinde tutulur; [üçüncü taraf aktarımı var/yok].
> *(POps'un GLPI'ye dışa aktarımı açıksa: bilgisayar adı, seri numarası, kurulu yazılımlar ve destek talepleri
> kurumun GLPI sunucusuna da aktarılır; talebi bildirenin adı yalnızca bu seçenek açıksa gider.)*
>
> **Haklarınız (KVKK m.11):** verilerinize erişme, düzeltilmesini/silinmesini isteme,
> işlemeye itiraz etme. Başvuru: **[KVKK başvuru adresi]**.

---

## 3. Kurumun yapması gerekenler (kontrol listesi)

- [ ] Saklama sürelerini POps tarafında yapılandır: ajan olay kayıtları ve tamamlanmış görevler varsayılan 365
      gün, okunmuş bildirimler 90 gün sonra her gece silinir (superadmin, `POST /api/system/retention`; 0 = süresiz).
      Güvenlik denetim kaydı (hash zincirli) silinmez; arşivleme planı: `docs/decisions.md` D-18.
- [ ] Ekran izleme için onay/bildirim akışının açık olduğunu doğrula (varsayılan açık).
- [ ] Reşit olmayanlar için veli/vasi bilgilendirmesini hazırla.
- [ ] Bu metni panele/giriş ekranına ve fiziksel laboratuvara asılabilir hale getir.
- [ ] Yıllık gözden geçirme.

## 4. POps'un uyumu kolaylaştıran özellikleri
- Gizli mod yok, keylogger yok; her uzaktan işlem bildirimli ve denetim kaydına tabi.
- (Yol haritası) Kullanıcı tarafında "son 30 günde bu bilgisayarda yapılan uzak işlemler"
  görünürlüğü — veri sahibine de denetim imkânı.
- (Yol haritası) Kapatılamayan oturum banner'ı: kim, ne zamandan beri, izliyor mu / kontrol mü.
