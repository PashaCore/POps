# Sunucu backend self-update (Faz 5)

Paneldeki **Sistem & Sürüm** sayfasından, SSH açmadan sunucu backend'ini
`origin/main`'den güncellemeyi sağlar. Tasarım ayrıcalık ayrımına dayanır:

```
Panel (superadmin)
   │  POST /api/system/self-update
   ▼
Backend  (pashacore_admin — ROOT DEĞİL)
   │  /var/lib/pops/deploy-request.json  ← yalnızca bu dosyayı yazabilir
   ▼
pops-selfupdate.path   (systemd, ROOT)  ← dosyanın belirmesini izler
   ▼
pops-selfupdate.service (systemd, ROOT, oneshot)
   │  git fetch + merge --ff-only origin/main
   ▼
/usr/local/sbin/pops-deploy-backend   ← sağlık kontrolü + otomatik geri dönüş
   ▼
/var/lib/pops/deploy-status.json  → panel durumu buradan okur
```

## Neden böyle?

- Backend'in kendisi **root değil**; kod indirip çalıştırmasına izin verilmez.
- İstek dosyasının **içeriği yürütülmez**, yalnızca tetikleyicidir. Güncelleme her
  zaman `origin/main`'i yeniden dağıtır — panel/istek üzerinden **keyfi kod
  çalıştırılamaz**.
- `pops-deploy-backend` zaten sağlık kontrolü yapıp başarısızlıkta eski `server.py`'ye
  otomatik döner; self-update bunu değiştirmez.
- Uç noktalar `require_superadmin` (tetikleme) / `require_admin` (durum) ile korunur.

## Kurulum (root, tek seferlik)

```bash
cd /home/pasha/domains/dev.pashacore.com.tr/public_html
sudo install -m 755 Installer/server/pops-selfupdate         /usr/local/sbin/pops-selfupdate
sudo install -m 644 Installer/server/pops-selfupdate.service /etc/systemd/system/
sudo install -m 644 Installer/server/pops-selfupdate.path    /etc/systemd/system/
sudo install -d -o pashacore_admin -g pashacore_admin -m 750 /var/lib/pops
sudo systemctl daemon-reload
sudo systemctl enable --now pops-selfupdate.path
```

`/var/lib/pops` backend servis kullanıcısına (`pashacore_admin`) ait olmalıdır; istek
dosyasını backend bu dizine yazar. Kurulmazsa uç nokta `503` döner ve panelde buton
"Kurulu değil" görünür — güvenli varsayılan.

## Çevrimdışı sunucu

`git fetch`/`ff-only` başarısız olursa (internet yok ya da yerelde ıraksama) betik
**mevcut commit'lenmiş HEAD** ile `pops-deploy-backend`'i çalıştırır. Yani internetsiz
kurulumda repo'yu elle güncelleyip (USB vb.) paneldeki butonla dağıtım yaptırabilirsiniz.
Ajan (MSI) güncellemesi ise ayrı, imzalı release yoluyla yürür (bkz. `SECURITY.md`).

## Sınırlar / notlar

- `merge --ff-only` kullanılır: yereldeki commit'ler **ezilmez**; ıraksama varsa git
  adımı atlanır ve mevcut kod yeniden dağıtılır.
- Ağır/yıkıcı bir DB migration'ı gelirse `pops-deploy-backend` içindeki nota göre
  restart öncesi `pg_dump` + `migrate.py` adımı eklenmelidir.
- Durum/kayıt: `/var/lib/pops/deploy-status.json` ve `/var/lib/pops/deploy.log`.
