#!/usr/bin/env python3
"""POps release imzalayıcı/doğrulayıcı (ed25519).

Bir release'in tüm dosyalarının SHA-256 özetlerini + sürüm + git tag + monoton
zaman damgasını tek bir `manifest.json` içinde toplar ve ed25519 ile imzalar
(`manifest.json.sig`, base64). Sürüm ve zaman damgası imzalı yükün İÇİNDE olduğu
için eski ama geçerli imzalı bir paketin (downgrade) tekrar oynatılması, doğrulayan
taraf "sürüm <= kuruluysa reddet" kuralını uygulayarak engellenebilir.

İmza, `manifest.json` dosyasının tam baytları üzerinedir (sort_keys + trailing \n ile
deterministik). Doğrulayıcı (bu araç, sunucu tarafı openssl/cryptography, ya da ajanda
BouncyCastle) aynı baytları doğrular.

Komutlar:
    genkey   --out-dir DIR                 ed25519 anahtar çifti üret (PEM)
    sign     --dir DIR --version V --tag T [--released-at N] --key-pem FILE|env
    verify   --dir DIR --pub-pem FILE      imzayı ve (varsa) dosya özetlerini doğrula
    selftest                               geçici anahtarla imzala/doğrula + kurcalama testi

Özel anahtar `sign` için --key-pem dosyası ya da POPS_RELEASE_PRIVATE_KEY ortam
değişkeninden (PEM içeriği) okunur. Python 3.9 uyumlu.
"""
import argparse
import base64
import glob
import hashlib
import json
import os
import sys
import time
from typing import Dict, List, Optional

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import (
    Ed25519PrivateKey,
    Ed25519PublicKey,
)

MANIFEST_NAME = "manifest.json"
SIG_NAME = "manifest.json.sig"
MANIFEST_SCHEMA = "pops-manifest/1"

# manifest/sig dosyalarının kendisi ve gizli/geçici dosyalar özetlenmez
_SKIP = {MANIFEST_NAME, SIG_NAME, "SHA256SUMS"}


def _sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _artifact_files(dir_path: str) -> List[str]:
    out = []
    for name in sorted(os.listdir(dir_path)):
        full = os.path.join(dir_path, name)
        if not os.path.isfile(full):
            continue
        if name in _SKIP or name.startswith("."):
            continue
        out.append(name)
    return out


def _canonical_bytes(manifest: Dict) -> bytes:
    """manifest.json'un imzalanan/yazılan tam baytları (deterministik)."""
    return (json.dumps(manifest, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")


def build_manifest(dir_path: str, version: str, tag: str, released_at: Optional[int]) -> Dict:
    if released_at is None:
        released_at = int(time.time())
    artifacts = []
    for name in _artifact_files(dir_path):
        full = os.path.join(dir_path, name)
        artifacts.append({"name": name, "sha256": _sha256(full), "size": os.path.getsize(full)})
    if not artifacts:
        sys.exit("sign: %s içinde imzalanacak dosya yok" % dir_path)
    return {
        "schema": MANIFEST_SCHEMA,
        "version": version,
        "tag": tag,
        "released_at": released_at,
        "artifacts": artifacts,
    }


def _load_private_key(key_pem: Optional[str]) -> Ed25519PrivateKey:
    if key_pem:
        with open(key_pem, "rb") as f:
            data = f.read()
    else:
        env = os.environ.get("POPS_RELEASE_PRIVATE_KEY")
        if not env:
            sys.exit("sign: --key-pem verilmedi ve POPS_RELEASE_PRIVATE_KEY tanımlı değil")
        data = env.encode("utf-8")
    key = serialization.load_pem_private_key(data, password=None)
    if not isinstance(key, Ed25519PrivateKey):
        sys.exit("sign: özel anahtar ed25519 değil")
    return key


def _load_public_key(pub_pem: str) -> Ed25519PublicKey:
    with open(pub_pem, "rb") as f:
        key = serialization.load_pem_public_key(f.read())
    if not isinstance(key, Ed25519PublicKey):
        sys.exit("verify: açık anahtar ed25519 değil")
    return key


def cmd_genkey(args) -> int:
    os.makedirs(args.out_dir, exist_ok=True)
    priv = Ed25519PrivateKey.generate()
    priv_pem = priv.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    )
    pub_pem = priv.public_key().public_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    priv_path = os.path.join(args.out_dir, "pops_release_ed25519.key.pem")
    pub_path = os.path.join(args.out_dir, "pops_release_ed25519.pub.pem")
    # özel anahtar 0600
    fd = os.open(priv_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as f:
        f.write(priv_pem)
    with open(pub_path, "wb") as f:
        f.write(pub_pem)
    raw = priv.public_key().public_bytes(
        encoding=serialization.Encoding.Raw, format=serialization.PublicFormat.Raw
    )
    print("Özel anahtar: %s (0600)" % priv_path)
    print("Açık anahtar: %s" % pub_path)
    print("Açık anahtar (ham 32 bayt, base64 — ajan gömme için): %s" % base64.b64encode(raw).decode())
    return 0


def cmd_sign(args) -> int:
    manifest = build_manifest(args.dir, args.version, args.tag, args.released_at)
    payload = _canonical_bytes(manifest)
    priv = _load_private_key(args.key_pem)
    sig = priv.sign(payload)
    with open(os.path.join(args.dir, MANIFEST_NAME), "wb") as f:
        f.write(payload)
    with open(os.path.join(args.dir, SIG_NAME), "wb") as f:
        f.write(base64.b64encode(sig) + b"\n")
    print("İmzalandı: %s + %s (%d dosya, sürüm %s, tag %s)"
          % (MANIFEST_NAME, SIG_NAME, len(manifest["artifacts"]), args.version, args.tag))
    return 0


def verify_manifest(dir_path: str, pub_pem: str, check_hashes: bool = True) -> Dict:
    """İmzayı doğrular; başarısızsa SystemExit. Doğrulanan manifest'i döndürür."""
    with open(os.path.join(dir_path, MANIFEST_NAME), "rb") as f:
        payload = f.read()
    with open(os.path.join(dir_path, SIG_NAME), "rb") as f:
        sig = base64.b64decode(f.read().strip())
    pub = _load_public_key(pub_pem)
    try:
        pub.verify(sig, payload)
    except InvalidSignature:
        sys.exit("verify: İMZA GEÇERSİZ — manifest kurcalanmış veya yanlış anahtar")
    manifest = json.loads(payload)
    if manifest.get("schema") != MANIFEST_SCHEMA:
        sys.exit("verify: bilinmeyen manifest şeması: %r" % manifest.get("schema"))
    if check_hashes:
        for art in manifest.get("artifacts", []):
            full = os.path.join(dir_path, art["name"])
            if not os.path.isfile(full):
                sys.exit("verify: manifest'teki dosya yok: %s" % art["name"])
            got = _sha256(full)
            if got != art["sha256"]:
                sys.exit("verify: SHA-256 uyuşmuyor: %s" % art["name"])
    return manifest


def cmd_verify(args) -> int:
    manifest = verify_manifest(args.dir, args.pub_pem, check_hashes=not args.no_hashes)
    print("OK: imza geçerli — sürüm %s, tag %s, %d dosya"
          % (manifest["version"], manifest["tag"], len(manifest["artifacts"])))
    return 0


def cmd_selftest(args) -> int:
    import tempfile
    d = tempfile.mkdtemp(prefix="pops-signtest-")
    # sahte artefaktlar
    for name, data in (("pops-server-9.9.9.tar.gz", b"server-bytes"),
                       ("POps-Agent-9.9.9-win-x64.zip", b"agent-bytes")):
        with open(os.path.join(d, name), "wb") as f:
            f.write(data)
    priv = Ed25519PrivateKey.generate()
    priv_pem = os.path.join(d, "k.key.pem")
    pub_pem = os.path.join(d, "k.pub.pem")
    fd = os.open(priv_pem, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as f:
        f.write(priv.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                   serialization.NoEncryption()))
    with open(pub_pem, "wb") as f:
        f.write(priv.public_key().public_bytes(serialization.Encoding.PEM,
                                                serialization.PublicFormat.SubjectPublicKeyInfo))

    class NS:
        pass
    s = NS(); s.dir = d; s.version = "9.9.9"; s.tag = "v9.9.9"; s.released_at = 1700000000; s.key_pem = priv_pem
    cmd_sign(s)
    verify_manifest(d, pub_pem)  # geçmeli
    print("selftest: temiz imza doğrulandı")

    # kurcalama: manifest'i boz -> doğrulama başarısız olmalı
    mpath = os.path.join(d, MANIFEST_NAME)
    with open(mpath, "rb") as f:
        body = f.read()
    with open(mpath, "wb") as f:
        f.write(body.replace(b'"9.9.9"', b'"9.9.8"'))
    try:
        verify_manifest(d, pub_pem)
        print("selftest: HATA — kurcalanmış manifest doğrulandı!", file=sys.stderr)
        return 1
    except SystemExit:
        print("selftest: kurcalanmış manifest doğru şekilde reddedildi")

    # artefakt özet uyuşmazlığı da yakalanmalı: sign'ı tazele, sonra dosyayı boz
    cmd_sign(s)
    with open(os.path.join(d, "agent-bytes-marker"), "wb") as f:
        f.write(b"x")
    with open(os.path.join(d, "POps-Agent-9.9.9-win-x64.zip"), "wb") as f:
        f.write(b"tampered")
    try:
        verify_manifest(d, pub_pem)
        print("selftest: HATA — bozuk artefakt doğrulandı!", file=sys.stderr)
        return 1
    except SystemExit:
        print("selftest: bozuk artefakt (hash) doğru şekilde reddedildi")

    for f in glob.glob(os.path.join(d, "*")):
        os.remove(f)
    os.rmdir(d)
    print("selftest: TÜM KONTROLLER GEÇTİ")
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    p = argparse.ArgumentParser(description="POps release imzalayıcı/doğrulayıcı (ed25519)")
    sub = p.add_subparsers(dest="cmd", required=True)

    g = sub.add_parser("genkey"); g.add_argument("--out-dir", required=True); g.set_defaults(fn=cmd_genkey)

    s = sub.add_parser("sign")
    s.add_argument("--dir", required=True)
    s.add_argument("--version", required=True)
    s.add_argument("--tag", required=True)
    s.add_argument("--released-at", type=int, default=None)
    s.add_argument("--key-pem", default=None)
    s.set_defaults(fn=cmd_sign)

    v = sub.add_parser("verify")
    v.add_argument("--dir", required=True)
    v.add_argument("--pub-pem", required=True)
    v.add_argument("--no-hashes", action="store_true")
    v.set_defaults(fn=cmd_verify)

    t = sub.add_parser("selftest"); t.set_defaults(fn=cmd_selftest)

    args = p.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
