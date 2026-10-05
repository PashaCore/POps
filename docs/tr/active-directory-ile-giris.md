# Active Directory ile giriş

Bu sayfa, öğretmenlerin ve BT personelinin POps paneline okulun Active Directory (AD) hesaplarıyla, yani her gün
Windows'a girdikleri kullanıcı adı ve şifreyle girmesini adım adım anlatır. Aynı ayarlar başka LDAP dizinleriyle
(OpenLDAP, Samba AD, FreeIPA) de çalışır. Sonda Microsoft Entra ID, Google Workspace ya da Keycloak ile giriş
(OpenID Connect) için kısa bir bölüm var.

Ayarların ayrıntısı: [`configuration.md`](../configuration.md#identity-providers). Güvenlik notları:
[`security.md`](../security.md#directory-and-single-sign-on).

## Nasıl çalışır

- Kullanıcı giriş ekranındaki **Kullanıcı adı** / **Şifre** formuna AD kullanıcı adını (`ayse.yilmaz`) ve AD şifresini
  yazar. POps şifreyi saklamaz; her girişte AD'ye sorar.
- Kim hangi rolle girer, AD gruplarıyla belirlenir: örneğin `POps-Yoneticiler` grubundakiler **Yönetici**,
  `POps-Izleyiciler` grubundakiler **İzleyici** olur. Eşlenen bir grupta olmayan giremez.
- İlk girişte panelde o kişi için bir kullanıcı kaydı açılır (yerel şifresi yoktur). Rol ve açabileceği sayfalar her
  girişte gruplardan yeniden yazılır; birini gruptan çıkarmak ya da AD'de devre dışı bırakmak yeter.
- **Yerel hesaplar çalışmaya devam eder.** Kurulumdaki `admin` gibi yerel hesaplar AD'ye hiç sorulmaz. AD'ye
  ulaşılamazsa AD hesapları giremez ("Dizin sunucusuna ulaşılamadı"), yerel süper admin her zaman girer. İlk yerel
  süper admin AD hesabına çevrilemez.
- Hesapta iki adımlı doğrulama (2FA) açıksa AD şifresinden sonra kod da istenir.

## Hazırlık (AD tarafında)

Bunlar bir etki alanı yöneticisinin (Domain Admin) işidir.

### 1. Gruplar

Panelde kullanılacak rol başına bir güvenlik grubu açın ve kişileri ekleyin:

| Grup (örnek) | Panelde |
| :--- | :--- |
| `POps-SuperAdmin` | Süper admin (her şey; küçük tutun, 2-3 kişi) |
| `POps-Yoneticiler` | Yönetici (günlük işler) |
| `POps-Izleyiciler` | İzleyici (yalnızca bakar) |

Grupların tam adını (DN) PowerShell'de alın; panelde buna ihtiyaç olacak:

```powershell
Get-ADGroup POps-Yoneticiler | Select-Object -ExpandProperty DistinguishedName
# CN=POps-Yoneticiler,OU=Gruplar,DC=okul,DC=local
```

### 2. Hizmet hesabı

POps'un kullanıcıları dizinde araması için ayrı, sıradan bir kullanıcı hesabı açın (yönetici yetkisi vermeyin;
`Domain Users` üyeliği okumak için yeter). Şifresinin süresi dolmasın, yoksa bir gün herkes girişte hata alır.

```powershell
New-ADUser -Name "pops-svc" -SamAccountName pops-svc -Path "OU=Servis,DC=okul,DC=local" `
  -AccountPassword (Read-Host -AsSecureString "Şifre") -Enabled $true -PasswordNeverExpires $true `
  -CannotChangePassword $true
Get-ADUser pops-svc | Select-Object -ExpandProperty DistinguishedName
```

### 3. LDAPS (şifreli bağlantı)

POps şifresiz LDAP'ı (389, TLS'siz) kabul etmez. Etki alanı denetleyicisinin (DC) bir sunucu sertifikası olmalı;
Active Directory Sertifika Hizmetleri (AD CS) kuruluysa DC'ler bunu kendiliğinden alır. POps sunucusundan sınayın:

```bash
openssl s_client -connect dc1.okul.local:636 -servername dc1.okul.local </dev/null | head -20
```

`Verify return code: 0 (ok)` görmüyorsanız sertifika POps sunucusunun güvendiği bir CA'dan değildir: kurumun kök
CA sertifikasını PEM olarak alın ve panelde **CA sertifikası** alanına yapıştırın. Kök CA'yı bir DC'de dışa
aktarmak için:

```powershell
certutil -ca.cert C:\kok-ca.cer
certutil -encode C:\kok-ca.cer C:\kok-ca.pem
```

636 kapalıysa StartTLS (389 üzerinden şifreli) de seçilebilir. Sunucu adını sertifikadakiyle aynı yazın
(`dc1.okul.local`); IP adresiyle bağlanırsanız ad uyuşmaz ve bağlantı reddedilir. POps sunucusunun DC'nin 636 (ya da
389) portuna ulaşabildiğinden emin olun.

## Panelde ayar

**Ayarlar** → **Güvenlik** → **Kimlik sağlayıcıları** → **Active Directory / LDAP** → **Ayarla** (yalnızca süper
admin):

| Alan | Örnek |
| :--- | :--- |
| Sunucu adı | `dc1.okul.local` |
| Port / Bağlantı | `636` / **LDAPS** (ya da `389` / **StartTLS**) |
| CA sertifikası | Gerekirse kök CA'nın PEM içeriği (`-----BEGIN CERTIFICATE-----` ile başlar) |
| Hizmet hesabı (DN) | `CN=pops-svc,OU=Servis,DC=okul,DC=local` |
| Hizmet hesabının şifresi | pops-svc'nin şifresi (kaydedildikten sonra gösterilmez; boş bırakılırsa kayıtlı şifre kalır) |
| Arama kökü (base DN) | `DC=okul,DC=local` |
| Kullanıcı filtresi | `(sAMAccountName={username})` |
| Kullanıcı adı özniteliği | `sAMAccountName` |
| Grup arama kökü / filtresi | Boş bırakılabilir: gruplar kullanıcının `memberOf` bilgisinden okunur. İç içe gruplar kullanıyorsanız grup arama kökünü (`OU=Gruplar,DC=okul,DC=local`) ve filtreyi `(member:1.2.840.113556.1.4.1941:={user_dn})` yapın. |

**Grup → rol** altında **Eşleme ekle** ile her grubun DN'ini yazın, rolünü seçin ve Yönetici ile İzleyici için
açabileceği sayfaları işaretleyin (Süper admin her sayfayı açar). Bir kişi birden çok gruptaysa en yüksek rol ve
bütün eşleşmelerin sayfaları geçerli olur.

Sonra:

1. **Bağlantıyı sına**'ya basın. "Bağlantı ve hizmet hesabı çalışıyor (LDAPS)" görmelisiniz. Kutuya bir kullanıcı adı
   yazıp yeniden basarsanız o kişinin DN'i, grupları ve girişte alacağı rol görünür (şifresi sorulmaz).
2. **Dizin hesaplarıyla girişe izin ver**'i açıp **Kaydet**'e basın. Değişiklik denetim kaydına yazılır (şifre
   yazılmaz).
3. Başka bir tarayıcıda (ya da gizli pencerede) bir AD kullanıcısıyla giriş yapın.

Kişiyi önceden hazırlamak isterseniz **Kullanıcılar** → **Kullanıcı ekle**'de **Kimlik kaynağı** olarak **Dizin
(LDAP)** seçip AD kullanıcı adını yazın; ilk girişte o kişiye bağlanır. Var olan bir yerel hesabı (ilk süper admin
dışında) aynı yoldan dizin hesabına çevirebilirsiniz; yerel şifresi silinir.

## Sık karşılaşılan sorunlar

| Görülen | Neden ve çözüm |
| :--- | :--- |
| "TLS doğrulanamadı: sunucu sertifikası güvenilir bir CA'dan değil" | Kök CA'nın PEM'ini **CA sertifikası** alanına yapıştırın. |
| "TLS doğrulanamadı: sertifikadaki ad sunucu adıyla uyuşmuyor" | Sunucu adını sertifikadaki adla aynı yazın (IP değil, `dc1.okul.local`). |
| "Hizmet hesabıyla bağlanılamadı" | Hizmet hesabının DN'i ya da şifresi yanlış, hesap kilitli ya da şifresinin süresi dolmuş. |
| "Kullanıcı bulunamadı" (sınamada) | Arama kökü kişiyi kapsamıyor ya da filtre yanlış. |
| Girişte "eşlenen bir grupta değil" | Kişi eşlenen gruplardan birinde değil ya da grup DN'i yanlış yazılmış (büyük/küçük harf önemsizdir). Sınamada kişinin gruplarına bakın. |
| Girişte "Bu hesap dizinde devre dışı bırakılmış" | AD'de hesap devre dışı. |
| "Arama kökü (base DN) dizinde yok ya da hizmet hesabı onu okuyamıyor" | Arama kökünü denetleyin; hizmet hesabının dizini okuma izni olmalı. |
| Girişte "Dizin sunucusuna ulaşılamadı" | POps sunucusu DC'ye ulaşamıyor (ağ, güvenlik duvarı, DC kapalı). Yerel hesaplar etkilenmez. |
| Sunucu, port, bağlantı türü, hizmet hesabı ya da CA sertifikası değişince "şifreyi yeniden girin" | Kayıtlı şifre başka bir sunucuya ya da daha zayıf bir bağlantıyla gönderilmesin diye bilerek böyledir; şifreyi yeniden yazın. |
| Şifre doğru ama "Geçersiz kullanıcı adı veya şifre" | Panelde aynı adlı yerel bir hesap var: yerel hesaplar dizine hiç sorulmaz, yerel şifre beklenir. O hesabın **Kimlik kaynağı**'nı **Dizin (LDAP)** yapın ya da hesabı silin. |
| "Bu kullanıcı adı panelde başka bir hesaba ait" | Panelde yalnızca büyük/küçük harfi farklı aynı adlı yerel bir hesap var (dizin hesabı yerel hesabı ele geçiremez), ya da AD'de silinip aynı adla yeniden açılan biri: paneldeki eski kaydı silin ya da **Kimlik kaynağı**'nı değiştirin. |
| `Domain Users` grubunu eşlediğim hâlde giremiyor | Birincil grup (`Domain Users`) `memberOf`'ta görünmez; ayrı bir güvenlik grubu kullanın. |
| Kimse giremiyor, ayarı düzeltecek süper admin de yok | Yerel süper adminle girin. O da yoksa sunucuda: `UPDATE sso_providers SET enabled = false;` |

## Microsoft Entra ID, Google ya da Keycloak (OpenID Connect)

Bulut hesaplarıyla giriş için giriş ekranına "**Okul hesabı ile giriş yap**" gibi bir düğme eklenir.

1. Sağlayıcıda POps için bir uygulama (istemci) kaydedin. Dönüş adresi (redirect URI):
   `https://<panel adresi>/api/auth/oidc/callback`. Entra ID'de grupların gelmesi için "groups" talebini
   (token configuration → groups claim) açın; Entra grup adını değil nesne kimliğini (GUID) gönderir.
2. **Ayarlar** → **Güvenlik** → **Kimlik sağlayıcıları** → **OpenID Connect** → **Ayarla**: düğmedeki ad, sağlayıcı
   adresi (Entra: `https://login.microsoftonline.com/<kiracı kimliği>/v2.0`, Google: `https://accounts.google.com`),
   istemci kimliği ve sırrı, dönüş adresi.
3. **Grup → rol** altında grup değerlerini (Entra: grup nesne kimliği) role eşleyin. Google grupları göndermez:
   **İzinli alan adları**'na okulun alan adını (`okul.k12.tr`) yazıp bir **varsayılan rol** (İzleyici ya da Yönetici)
   seçebilirsiniz; doğrulanmış e-postası o alan adında olan herkes o rolle girer.
4. **Bağlantıyı sına**, ardından **Giriş ekranında sağlayıcı düğmesini göster** ve **Kaydet**.

Kullanıcı adı varsayılan olarak e-posta adresidir (`ayse.yilmaz@okul.k12.tr`) ve yalnızca sağlayıcı e-postayı
doğrulanmış olarak bildiriyorsa (`email_verified`) kabul edilir. Microsoft Entra ID bunu göndermez: Entra'da
**Kullanıcı adı talebi** alanına `preferred_username` (ya da `upn`) yazın.
