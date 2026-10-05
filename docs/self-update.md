# Sunucu backend self-update (Faz 5)

Paneldeki **Sistem** sayfasından, SSH açmadan sunucu backend'ini
güncellemeyi sağlar. Tasarım ayrıcalık ayrımına dayanır:

```
Panel (superadmin)
   │  POST /api/system/self-update
   ▼
Backend  (servis kullanıcısı — ROOT DEĞİL)
   │  /var/lib/pops/deploy-request.json  ← yalnızca bu dosyayı yazabilir
   ▼
pops-selfupdate.path   (systemd, ROOT)  ← dosyanın belirmesini izler
   ▼
pops-selfupdate.service (systemd, ROOT, oneshot)
   │  git fetch → (release) etiket imzasını doğrula → merge --ff-only
   ▼
/usr/local/sbin/pops-deploy-backend   ← sağlık kontrolü + otomatik geri dönüş (kod + venv)
   ▼
/var/lib/pops-state/deploy-status.json  → panel durumu buradan okur (dizin root'a ait)
```

## Neden böyle?

- Backend'in kendisi **root değil**; kod indirip çalıştırmasına izin verilmez.
- İstek dosyasının **içeriği yürütülmez**, yalnızca tetikleyicidir. Güncelleme her
  zaman `origin/main` üzerindeki bir sürümü (ya da `main` kanalında `origin/main`'i) yeniden dağıtır — panel/istek
  üzerinden **keyfi kod çalıştırılamaz**.
- Sürüm etiketleri SSH ile imzalanır; `/etc/pops/allowed_signers` varsa sunucu imzası doğrulanamayan etikete
  geçmez ([aşağıda](#sürüm-etiketlerinin-imzası)). Böylece GitHub'a yazabilen biri sunucuya kendiliğinden kod
  dağıtamaz.
- `pops-deploy-backend` sağlık kontrolü yapar; ilk değişiklikten sonraki her hatada (pip, dosya kopyalama, restart,
  sağlık kontrolü) önceki kod setine ve requirements değiştiyse pip öncesi venv'e (tarball yedekleri) birebir
  döner; self-update bunu değiştirmez.
- Venv'in Python'u yeni sürümün istediğinden (`requirements.txt`, `# requires-python`) eskiyse deploy sunucudaki
  `python3.12`/`python3.11`/`python3.10` ile yeni bir venv kurar ve yerine koyar; hata olursa eski venv geri gelir.
  Uygun Python yoksa hiçbir şeye dokunmadan durur (çıkış 3) ve paneldeki durum "python3.12 kurun" der. Mevcut bir
  AlmaLinux/RHEL 9 sunucusunu taşımak: [deployment.md](deployment.md#moving-an-existing-server-to-python-312).
- Uç noktalar `require_superadmin` (tetikleme) / `require_admin` (durum) ile korunur.

## Kanal: sürüm (varsayılan) ya da main

- **`release` (varsayılan):** Sunucu yalnızca **yayımlanmış sürümlere** geçer: `origin/main` üzerindeki en yeni
  `v*` etiketi (sürüm sırasıyla; `0.1.10` > `0.1.9`). Sunucu zaten o sürümde ya da daha yenisindeyse git adımı
  atlanır, **geri gidilmez**. Panel "Yeni sürüm: vX" yazar, ara commit'ler sayılmaz. Okullar için doğru ayar.
- **`main`:** `origin/main`'in son hali (geliştirme sunucusu). Panel commit farkını gösterir. Etiket imzası
  denetlenmez (logda yazar).

Ayarlar root'a ait `/etc/pops/selfupdate.conf` dosyasındadır (dosya yoksa aşağıdaki varsayılanlar). Panel kanalı
değiştiremez; backend dosyayı yalnızca kanalı göstermek için okur, bu yüzden 644 kalmalıdır.

```bash
CHANNEL=release                            # release | main
ALLOWED_SIGNERS=/etc/pops/allowed_signers  # sürüm etiketini imzalayabilecek SSH anahtarları
REQUIRE_SIGNED_TAGS=0                      # 1: allowed_signers olmasa da imzasız etikete geçme
```

Checkout'un yolu (`REPO`) `pops-deploy-backend` ile aynı dosyadan, `/etc/pops/deploy.conf`'tan okunur
([`deployment.md`](deployment.md#etcpopsdeployconf)). Her iki ayar dosyası da kabukla okunduğu için root'a ait,
grup/diğerleri tarafından yazılamaz ve sembolik bağ olmayan bir dosya olmalıdır (`/etc/pops` dizini de); değilse
betik okumayı reddeder, durumu `failed` / "ayar dosyasi guvenli degil" yapar ve hiçbir şey dağıtmaz.

Denemek için: `sudo POPS_SELFUPDATE_DRYRUN=1 pops-selfupdate` (hedefi ve imza sonucunu yazar:
`DRYRUN kanal=release hedef=vX imza=gecerli|gecersiz|denetlenmedi HEAD=...`; depoyu ileri sarmaz, durum dosyasına
dokunmaz, hiçbir şey dağıtmaz).

## Sürüm etiketlerinin imzası

`CHANNEL=release`'te, en yeni etikete geçmeden önce:

| Durum | Sonuç |
| --- | --- |
| `allowed_signers` var, etiket listedeki bir anahtarla SSH imzalı | geçilir, deploy yapılır |
| `allowed_signers` var, etiket imzasız, hafif (lightweight), GPG imzalı ya da başka anahtarla imzalı | **merge ve deploy yapılmaz**; durum `failed` / "etiket imzasi dogrulanamadi (vX)", ayrıntı `deploy.log`'da |
| `allowed_signers` var ama root'a ait değil ya da başkaları yazabiliyor | dosyaya güvenilmez: yukarıdaki gibi durulur |
| `allowed_signers` yok, `REQUIRE_SIGNED_TAGS=1` | durulur |
| `allowed_signers` yok, `REQUIRE_SIGNED_TAGS=0` (eski kurulumların varsayılanı) | geçilir; logda "imzasız etiket" uyarısı |

Dosyanın varlığı zorlamayı kendiliğinden açar: anahtarı kurup `REQUIRE_SIGNED_TAGS`'i unutan bir sunucu korumasız
kalmaz. `REQUIRE_SIGNED_TAGS=1` ek olarak dosya silinirse de durmayı sağlar. Yalnızca **en yeni** etiket denetlenir:
o imzasızsa sunucu, imzalı daha yeni bir etiket gelene kadar mevcut sürümde kalır. Çevrimdışı sunucuda (fetch
başarısız) git adımı yoktur; mevcut checkout yeniden dağıtılır.

Bu SSH anahtarı, ajan paketlerini imzalayan ed25519 release anahtarından (`keys/`, GitHub secret) **ayrıdır** ve
yalnızca etiketi atan geliştiricide durur; sunucuya yalnızca açık anahtarı konur.

### 1. İmza anahtarı (etiketi atan geliştiricide, bir kez)

```bash
ssh-keygen -t ed25519 -C "pops-release-tag" -f ~/.ssh/pops_release_tag   # parola verin
```

### 2. git ayarı (repo içinde)

```bash
git config gpg.format ssh
git config user.signingkey ~/.ssh/pops_release_tag.pub
```

### 3. Etiketi imzalı atmak

```bash
git tag -s vX.Y.Z -m "vX.Y.Z"
git push origin vX.Y.Z
```

Yerelde doğrulamak için: `git -c gpg.ssh.allowedSignersFile=<allowed_signers> verify-tag vX.Y.Z`.

### 4. Sunucuda `/etc/pops/allowed_signers`

Her satır bir anahtardır: `<e-posta> namespaces="git" <anahtar türü> <açık anahtar>`, örneğin

```
release@example.org namespaces="git" ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI...
```

```bash
printf '%s namespaces="git" %s\n' release@example.org "$(cut -d' ' -f1,2 pops_release_tag.pub)" \
    | sudo tee /etc/pops/allowed_signers >/dev/null
sudo chown root:root /etc/pops/allowed_signers && sudo chmod 644 /etc/pops/allowed_signers
```

### 5. Zorlamayı açmak

1. Dosyayı koymadan önce son etiketin imzalı olduğundan emin olun (`git verify-tag`, yukarıda).
2. Dosyayı koyun (zorlama bu anda açılır), sonra `sudo POPS_SELFUPDATE_DRYRUN=1 pops-selfupdate`: son satırda
   `imza=gecerli` görünmeli (sunucu zaten en son sürümdeyse imza denetlenmez, `imza=yok` yazar).
3. İsteğe bağlı: `/etc/pops/selfupdate.conf`'a `REQUIRE_SIGNED_TAGS=1`.

Anahtar değiştirmek: yeni anahtarın satırını ekleyin, yeni etiketleri onunla imzalayın, sonra eski satırı silin.

## Kurulum (root, tek seferlik)

```bash
cd <checkout>            # deploy.conf'taki REPO
sudo install -m 755 Installer/server/pops-deploy-backend      /usr/local/sbin/pops-deploy-backend
sudo install -m 755 Installer/server/pops-selfupdate         /usr/local/sbin/pops-selfupdate
sudo install -m 644 Installer/server/pops-selfupdate.service /etc/systemd/system/
sudo install -m 644 Installer/server/pops-selfupdate.path    /etc/systemd/system/
sudo install -d -m 755 /etc/pops
# install.sh ile kurulduysa /etc/pops/deploy.conf zaten var; yoksa şablondan yazıp yolları düzeltin
[ -f /etc/pops/deploy.conf ] || sudo install -m 644 Installer/server/deploy.conf.example /etc/pops/deploy.conf
sudo install -d -o <servis-kullanıcısı> -g <servis-kullanıcısı> -m 750 /var/lib/pops
sudo install -d -o root -g root -m 755 /var/lib/pops-state
sudo systemctl daemon-reload
sudo systemctl enable --now pops-selfupdate.path
```

`/var/lib/pops` backend servis kullanıcısına (`install.sh` ile `pops`; deploy.conf'taki `OWNER`) ait olmalıdır;
istek dosyasını backend bu dizine yazar. `/var/lib/pops-state` ise root'a aittir; betik durumu ve logu oraya yazar
(betik dizini yoksa kendisi oluşturur, başka bir kullanıcıya aitse durur). Kurulmazsa uç nokta `503` döner ve panelde buton
"Kurulu değil" görünür — güvenli varsayılan.

`/usr/local/sbin` altındaki kopyalar repo güncellenince **değişmez**: bu betikleri değiştiren bir sürümden sonra
yukarıdaki `install` satırlarını yeniden çalıştırın (CHANGELOG bunu belirtir).

## Çevrimdışı sunucu

`git fetch`/`ff-only` başarısız olursa (internet yok ya da yerelde ıraksama) betik
**mevcut commit'lenmiş HEAD** ile `pops-deploy-backend`'i çalıştırır. Yani internetsiz
kurulumda repo'yu elle güncelleyip (USB vb.) paneldeki butonla dağıtım yaptırabilirsiniz.
Ajan (MSI) güncellemesi ise ayrı, imzalı release yoluyla yürür (bkz. `SECURITY.md`).

## Sınırlar / notlar

- `merge --ff-only` kullanılır: yereldeki commit'ler **ezilmez**; ıraksama varsa git
  adımı atlanır ve mevcut kod yeniden dağıtılır. İmzası doğrulanamayan etikette ise hiçbir şey dağıtılmaz.
- Ağır/yıkıcı bir DB migration'ı gelirse `pops-deploy-backend` içindeki nota göre
  restart öncesi `pg_dump` + `migrate.py` adımı eklenmelidir. Geri dönüş migration'ları geri almaz.
- Durum/kayıt: `/var/lib/pops-state/deploy-status.json` ve `/var/lib/pops-state/deploy.log`. Bu dizin
  yalnızca root'a aittir: root, backend kullanıcısının yazabildiği bir dizine yazmaz (oraya konmuş bir sembolik
  bağ, root'a istenen dosyayı yazdırabilirdi). 0.1.12 ve öncesinden kalan `/var/lib/pops/deploy-status.json`,
  `deploy.log` ve `backup-status.json` silinebilir.
- Testler: `bash Installer/server/tests/test_deploy.sh` (root gerekmez; CI'da "Server scripts" işi).
