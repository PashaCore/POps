"""POps release manifest doğrulama (ed25519) — sunucu tarafı, server.py'yi import etmez.

`tools/sign_release.py`'nin ürettiği `manifest.json` + `manifest.json.sig` çiftini
depodaki açık anahtarla (`keys/pops_release_ed25519.pub.pem`) doğrular ve manifest'teki
SHA-256 özetlerini yerel dosyalarla karşılaştırır. openssl ile üretilen imzalarla
da uyumludur (aynı ham ed25519). Python 3.9 uyumlu.

NOT: Bu, uygulama seviyesindeki (upload-release) doğrulamadır ve açık anahtarı
uygulamanın okuyabildiği yerden okur. Root ile çalışan sunucu-kendini-güncelleme
yolunda (Faz 5) anahtar root'a ait ayrı bir konumdan (ör. /etc/pops/release_pub.pem)
okunmalıdır ki ele geçirilmiş uygulama anahtarı değiştiremesin.
"""
import base64
import hashlib
import json
from typing import Dict, Optional

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

MANIFEST_SCHEMA = "pops-manifest/1"


class ReleaseVerifyError(Exception):
    """Doğrulama başarısız (imza, şema veya özet uyuşmazlığı)."""


def _load_pub(pubkey_pem_path: str) -> Ed25519PublicKey:
    with open(pubkey_pem_path, "rb") as f:
        key = serialization.load_pem_public_key(f.read())
    if not isinstance(key, Ed25519PublicKey):
        raise ReleaseVerifyError("acik anahtar ed25519 degil")
    return key


def verify_manifest(manifest_bytes: bytes, sig_b64: bytes, pubkey_pem_path: str) -> Dict:
    """İmzayı doğrular ve ayrıştırılmış manifest'i döndürür. Başarısızsa ReleaseVerifyError."""
    pub = _load_pub(pubkey_pem_path)
    try:
        sig = base64.b64decode(sig_b64.strip())
    except Exception:
        raise ReleaseVerifyError("imza base64 cozulemedi")
    try:
        pub.verify(sig, manifest_bytes)
    except InvalidSignature:
        raise ReleaseVerifyError("imza gecersiz (paket kurcalanmis veya yanlis anahtar)")
    try:
        manifest = json.loads(manifest_bytes)
    except Exception:
        raise ReleaseVerifyError("manifest gecerli JSON degil")
    if manifest.get("schema") != MANIFEST_SCHEMA:
        raise ReleaseVerifyError("bilinmeyen manifest semasi: %r" % manifest.get("schema"))
    if not isinstance(manifest.get("artifacts"), list) or not manifest.get("version"):
        raise ReleaseVerifyError("manifest eksik alan (artifacts/version)")
    return manifest


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def artifact_entry(manifest: Dict, name: str) -> Optional[Dict]:
    for art in manifest.get("artifacts", []):
        if art.get("name") == name:
            return art
    return None
