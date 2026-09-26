# POps release imza anahtarları

POps release'leri ed25519 ile imzalanır. Her release'e, tüm paketlerin SHA-256
özetlerini + sürüm + git tag + zaman damgasını içeren `manifest.json` ve onun
imzası `manifest.json.sig` eklenir (bkz. [`tools/sign_release.py`](../tools/sign_release.py)).

## Bu klasördeki dosya

- `pops_release_ed25519.pub.pem` — **açık** anahtar (PEM). Repoda durur, sunucu
  paketiyle dağıtılır ve doğrulama yapan taraflar bunu kullanır. Paylaşılması güvenlidir.

Açık anahtarın ham 32 baytı (ajana gömmek için, base64):

```
MQqVu9JHcHvpj0gI8FcrrtrqrCxSe4iAqKR2L/bZYuQ=
```

## Özel anahtar

Özel anahtar **repoda tutulmaz** (`.gitignore`: `*.key.pem`). Yalnızca GitHub Actions
release iş akışında, `POPS_RELEASE_PRIVATE_KEY` adlı repository secret'ından (PEM içeriği)
okunur. Sunucuda ya da ajanda özel anahtar bulunmaz — onlar yalnızca açık anahtarla doğrular.

Yeni bir anahtar üretmek için:

```bash
python tools/sign_release.py genkey --out-dir keys
```

Bu, `keys/pops_release_ed25519.key.pem` (özel, 0600) ve `keys/pops_release_ed25519.pub.pem`
(açık) üretir. Özel PEM'in **tüm içeriğini** GitHub → Settings → Secrets and variables →
Actions → `POPS_RELEASE_PRIVATE_KEY` olarak ekleyin, ardından yerel özel anahtar dosyasını silin.
Açık anahtarı (`.pub.pem`) commit'leyin.

## Doğrulama (elle)

```bash
python tools/sign_release.py verify --dir <release-dosyalari> --pub-pem keys/pops_release_ed25519.pub.pem
# ya da openssl ile:
base64 -d manifest.json.sig > sig.raw
openssl pkeyutl -verify -pubin -inkey keys/pops_release_ed25519.pub.pem -rawin -in manifest.json -sigfile sig.raw
```

## Rotasyon

Anahtar değişince: yeni açık anahtarı buraya commit'leyin, yeni özel anahtarı secret'a
yazın ve doğrulayan tüm tarafların (sunucu paketi + gömülü ajan anahtarı) yeni açık
anahtarı aldığından emin olun. Eski imzalar yeni anahtarla doğrulanmaz.
