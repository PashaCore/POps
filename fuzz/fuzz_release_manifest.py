#!/usr/bin/env python3
"""Fuzz hedefi: imzalı sürüm manifest'inin doğrulanması (Backend/release_verify.py; upload-release ve fetch-release).

Girdinin ilk baytı kipi seçer, gerisi manifest baytlarıdır:
  0  manifest hedefin geçici anahtarıyla imzalanır: imza geçer, JSON ve şema denetimleri çalışır
  1  imza da girdiden gelir (ilk satır, base64), manifest kalan baytlar
  2  manifest imzalanır, sonra imzalanan baytlardan biri değiştirilir (kurcalanmış paket)

Denetlenen: verify_manifest() ya doğrulanmış bir manifest döndürür ya da ReleaseVerifyError fırlatır, başka hiçbir
şey; yalnızca anahtarın tam bu baytlar için verdiği imza kabul edilir (kip 1 ve 2 hep reddedilir); kabul edilen
manifest, çağıranların (system_routes.py) güvendiği alanlarda docs/protocol/release-manifest.json'a uyar: nesne,
şema "pops-manifest/1", metin sürüm, varsa tam sayı released_at, her artefakt metin name ve sha256 taşıyan bir
nesne; artifact_entry() her adda bir artefakt ya da None döndürür.

    python fuzz/fuzz_release_manifest.py -max_total_time=60 <yeni-girdiler-klasörü> fuzz/corpus/release_manifest
"""

import base64
import hashlib

import common

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey  # noqa: E402

with common.backend_imports():
    import release_verify

KEY = Ed25519PrivateKey.generate()
PUB = "fuzz-test-key"
# Açık anahtar dosyası (operatörün dosyası, güvenilmeyen girdi değil) okunmaz: geçici dosya bırakılmasın
release_verify._load_pub = lambda path: KEY.public_key()
NAMES = ("pops-server-9.9.9.tar.gz", "POps-Agent-9.9.9-win-x64.msi", "manifest.json", "")


def TestOneInput(data):
    if not data:
        return
    mode, manifest = data[0] % 3, data[1:]
    if mode == 1:
        sig, _, manifest = manifest.partition(b"\n")
    else:
        sig = base64.b64encode(KEY.sign(manifest))
        if mode == 2:
            if not manifest:
                return
            i = int.from_bytes(hashlib.sha256(manifest).digest()[:4], "big") % len(manifest)
            manifest = manifest[:i] + bytes([manifest[i] ^ 0x01]) + manifest[i + 1:]
    try:
        verified = release_verify.verify_manifest(manifest, sig, PUB)
    except release_verify.ReleaseVerifyError:
        return
    assert mode == 0, "imzası tutmayan manifest kabul edildi (kip %d)" % mode
    assert isinstance(verified, dict), "manifest nesne değil: %r" % type(verified)
    assert verified.get("schema") == release_verify.MANIFEST_SCHEMA
    assert isinstance(verified.get("version"), str) and verified["version"], "version: %r" % verified.get("version")
    released_at = verified.get("released_at", 0)
    assert isinstance(released_at, int) and not isinstance(released_at, bool), "released_at: %r" % (released_at,)
    assert isinstance(verified.get("artifacts"), list)
    for art in verified["artifacts"]:
        assert isinstance(art, dict) and isinstance(art.get("name"), str) and isinstance(art.get("sha256"), str), (
            "artefakt: %r" % (art,))
    for name in NAMES + tuple(a["name"] for a in verified["artifacts"]):
        entry = release_verify.artifact_entry(verified, name)
        assert entry is None or isinstance(entry, dict), "artifact_entry: %r" % (entry,)


if __name__ == "__main__":
    common.run(TestOneInput)
