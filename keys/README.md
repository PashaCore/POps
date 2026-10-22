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

Ajan açık anahtarı **içine gömülü** taşır (`ReleaseVerifier.PublicKeyBase64`) ve yalnızca onunla imzalanmış
güncellemeyi kabul eder. Bu yüzden anahtar tek adımda değiştirilemez: yeni anahtarla imzalanmış bir sürümü sahadaki
ajanlar reddeder.

**Planlı değişim** (ör. yıllık ya da bakımcı değişince):

1. Yeni anahtar çiftini üretin (`genkey`); özel anahtarı henüz secret'a yazmayın.
2. Ajanı **iki anahtarı da** kabul edecek şekilde değiştirin ve bu sürümü **eski** anahtarla yayımlayın. Sunucu
   paketine de iki açık anahtarı koyun. (Ajanda iki anahtar desteği henüz yok; rotasyondan önce eklenmeli.)
3. Bütün ajanlar bu sürüme geçene kadar bekleyin (panel: Cihazlar → sürüm sütunu).
4. Secret'ı yeni özel anahtarla değiştirin; sonraki sürüm yeni anahtarla imzalanır.
5. Bir sonraki sürümde eski açık anahtarı ajandan ve sunucudan kaldırın.

**Anahtar sızarsa:** secret'ı hemen silin (yeni sürüm çıkmasın) ve yeni anahtar üretin. Sızan anahtarla imzalanmış
bir manifest yine de yalnızca ajanın kendi sunucusunun emriyle gelir (paket o sunucudan ya da sunucunun bildirdiği
laboratuvar eşlerinden iner ve manifest'teki SHA-256'yla denetlenir), yani saldırganın ayrıca sunucuyu da ele
geçirmesi gerekir. Sahadaki ajanları yeni anahtara taşımanın iki yolu var: yukarıdaki iki anahtarlı sürümü (sızan anahtar
iptal edilmeden önce son kez onunla imzalayarak) dağıtmak ya da yeni anahtarlı MSI'ı elle/GPO ile yeniden kurmak.
Sunucunun kendini güncellemesi etiket imzasına bağlıysa (`/etc/pops/allowed_signers`, bkz.
[`docs/self-update.md`](../docs/self-update.md)) etiket anahtarı bundan ayrıdır ve ayrıca değiştirilir.

## Sürüm etiketlerinin imzası (SSH)

`v0.1.22-alpha`'dan itibaren sürüm etiketleri ayrı bir SSH anahtarıyla imzalanır (`git tag -s`, `gpg.format=ssh`).

- `pops_tag_signing.pub`: etiket imza anahtarının **açık** hali
  (`SHA256:wBv1/5TQgllT+gLzCicQC5DFIxKQYKXLflLfb7MEB8w`).
- `allowed_signers`: sunucuda `/etc/pops/allowed_signers` olarak kurulacak satır. Kurulunca kendini güncelleme
  yalnızca bu anahtarla imzalı etikete geçer (bkz. [`docs/self-update.md`](../docs/self-update.md)).

Özel anahtar repoda tutulmaz; proje sunucusunda root'a ait (0600) durur. Anahtar değişirse bu iki dosya ve
sunuculardaki `allowed_signers` birlikte güncellenir, sürüm notunda yazılır.

Bir etiketi doğrulamak için: `git -c gpg.ssh.allowedSignersFile=keys/allowed_signers verify-tag v0.1.22-alpha`.

